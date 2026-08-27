using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations;
using InvoiceManager.Infrastructure.Http;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.Extensions.Logging;

namespace InvoiceManager.Integrations.Microsoft365;

/// <summary>
/// Retrieves invoices carried as PDF attachments on recurring emails, via the
/// same delegated Microsoft Graph mailbox used for OneDrive uploads. Candidate
/// emails are found by sender address, a date window around the expected
/// invoice date, and an optional body regex; the winning PDF attachment's
/// invoice date and total are then read via <see cref="IInvoicePdfExtractor"/>
/// (VAT mode is never derived here — it always comes from configuration).
/// </summary>
public sealed class GraphEmailInvoiceSource(
    HttpClient httpClient,
    IMicrosoftTokenProvider tokenProvider,
    IInvoicePdfExtractor pdfExtractor,
    ILogger<GraphEmailInvoiceSource> logger) : IInvoiceSourceIntegration
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private static readonly string[] Scopes = ["https://graph.microsoft.com/Mail.Read"];
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public IntegrationType IntegrationType => IntegrationType.GraphEmail;

    public async Task<InvoiceSourceResult> FindInvoiceAsync(
        InvoiceSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        if (criteria.IntegrationConfiguration is not GraphEmailIntegrationConfiguration email)
            throw new InvalidOperationException(
                $"{nameof(GraphEmailInvoiceSource)} received criteria for an unsupported integration configuration.");

        using var activity = Telemetry.ActivitySource.StartActivity("find_invoice.microsoft365email");
        activity?.SetTag("invoice.sender_email_address", email.SenderEmailAddress);
        activity?.SetTag("invoice.expected_date", criteria.ExpectedDate.ToString("O"));
        activity?.SetTag("invoice.date_tolerance_days", criteria.DateToleranceDays);

        var token = await tokenProvider.AcquireTokenAsync(Scopes, cancellationToken);

        var candidates = await ListCandidateMessagesAsync(email, criteria, token, cancellationToken);
        activity?.SetTag("invoice.candidate_count", candidates.Count);

        // A technical inability to read a PDF is only escalated to a failure (mapped
        // by the caller to RetrievalError) once every candidate has been tried — a
        // failure on the closest candidate must not prevent a further-but-still-in-window
        // candidate from being tried.
        var extractionFailures = new List<string>();

        // Closest to the expected date first, mirroring the closest-match preference
        // used by the other source/OneDrive matchers.
        foreach (var message in candidates.OrderBy(m => criteria.DateDistanceDays(DateOnly.FromDateTime(m.ReceivedDateTime.UtcDateTime))))
        {
            var attachments = await ListPdfAttachmentsAsync(message.Id, token, cancellationToken);
            if (attachments.Count == 0)
            {
                activity?.AddEvent(new ActivityEvent("matched_email_has_no_pdf"));
                logger.LogWarning(
                    "Email {MessageId} from {Sender} matched criteria but has no PDF attachment; skipping.",
                    message.Id, email.SenderEmailAddress);
                continue;
            }

            var outcome = await TryExtractMatchingInvoiceAsync(message, attachments, criteria, token, cancellationToken);

            if (outcome is EmailInvoiceExtractionFailed extractionFailed)
            {
                extractionFailures.Add($"message {message.Id}: {extractionFailed.Reason}");
                continue;
            }

            if (outcome is not EmailInvoiceFound found)
                continue;

            activity?.SetTag("invoice.matched_message_id", message.Id);
            activity?.AddEvent(new ActivityEvent("match_selected"));
            logger.LogInformation(
                "Retrieved GraphEmail invoice from message {MessageId} ({PdfBytes} bytes).",
                message.Id, found.PdfContent.Length);

            var details = new ActualInvoiceDetails(
                found.Extraction.InvoiceDate,
                found.Extraction.Total,
                new SourceInvoiceId(Path.GetFileNameWithoutExtension(found.AttachmentName)));

            return new InvoiceMatch(found.PdfContent, details);
        }

        if (extractionFailures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{extractionFailures.Count} candidate email(s) had PDF attachment(s) that could not be read as " +
                $"an invoice: {string.Join("; ", extractionFailures)}.");
        }

        var diagnostic = BuildNoMatchDiagnostic(email, criteria, candidates.Count);
        activity?.AddEvent(new ActivityEvent("no_match"));
        logger.LogInformation(
            "No email matched criteria for sender {Sender} around {ExpectedDate}: {Diagnostic}",
            email.SenderEmailAddress, criteria.ExpectedDate, diagnostic);
        return new NoInvoiceMatch(diagnostic);
    }

    /// <summary>
    /// Tries every PDF attachment on the message, accepting the first one that both
    /// extracts successfully and satisfies <paramref name="criteria"/>'s date/amount
    /// tolerances. An attachment that extracts but doesn't satisfy criteria is not a
    /// technical failure — it is simply the wrong invoice, so other messages should
    /// still be tried without raising an error. Only a PDF that cannot be read at all
    /// is a technical failure.
    /// </summary>
    private async Task<EmailInvoiceOutcome> TryExtractMatchingInvoiceAsync(
        GraphMessage message,
        IReadOnlyList<GraphAttachment> pdfAttachments,
        InvoiceSearchCriteria criteria,
        string token,
        CancellationToken cancellationToken)
    {
        var readFailures = new List<string>();

        foreach (var attachment in pdfAttachments)
        {
            var content = await GetAttachmentContentAsync(message.Id, attachment, token, cancellationToken);
            var result = await pdfExtractor.ExtractAsync(content, cancellationToken);

            if (result is PdfExtractionSucceeded succeeded)
            {
                if (!criteria.Matches(succeeded.InvoiceDate, succeeded.Total))
                    continue;

                var sourceInvoiceId = Path.GetFileNameWithoutExtension(attachment.Name);
                if (!IsValidSourceInvoiceId(sourceInvoiceId))
                {
                    readFailures.Add(
                        $"{attachment.Name}: attachment name '{sourceInvoiceId}' cannot be used as a " +
                        "SourceInvoiceId (it contains whitespace, so the canonical OneDrive filename " +
                        "would not round-trip through parsing)");
                    continue;
                }

                return new EmailInvoiceFound(content, succeeded, attachment.Name);
            }

            var reason = result is PdfExtractionFailed failed ? failed.Reason : "unknown extraction failure";
            readFailures.Add($"{attachment.Name}: {reason}");
        }

        return readFailures.Count > 0
            ? new EmailInvoiceExtractionFailed(
                $"{readFailures.Count} of {pdfAttachments.Count} PDF attachment(s) could not be read: {string.Join("; ", readFailures)}")
            : new EmailInvoiceCriteriaMismatch();
    }

    /// <summary>
    /// Explains why no candidate email's PDF attachment satisfied the criteria: the
    /// sender, date window, optional body pattern, expected amount/tolerance (if
    /// configured), and how many candidate emails were considered in the window.
    /// </summary>
    private static string BuildNoMatchDiagnostic(
        GraphEmailIntegrationConfiguration email, InvoiceSearchCriteria criteria, int candidateCount)
    {
        var windowStart = criteria.ExpectedDate.AddDays(-criteria.DateToleranceDays);
        var windowEnd = criteria.ExpectedDate.AddDays(criteria.DateToleranceDays);
        var amountDescription = criteria.AmountMatchingCriteria is AmountMatchingCriteria amc
            ? $"{amc.Amount.Amount} {amc.Amount.Currency.Code} (tolerance {amc.AmountTolerance})"
            : "any amount";
        var bodyPatternDescription = string.IsNullOrEmpty(email.BodyPattern)
            ? "no body pattern configured"
            : $"body pattern '{email.BodyPattern}'";

        return candidateCount == 0
            ? $"No email from {email.SenderEmailAddress} received between {windowStart:yyyy-MM-dd} and " +
                $"{windowEnd:yyyy-MM-dd} matched {bodyPatternDescription}."
            : $"{candidateCount} email(s) from {email.SenderEmailAddress} matched the date window " +
                $"({windowStart:yyyy-MM-dd} to {windowEnd:yyyy-MM-dd}) and {bodyPatternDescription}, but none had a " +
                $"readable PDF attachment matching the expected amount {amountDescription}.";
    }

    private async Task<IReadOnlyList<GraphMessage>> ListCandidateMessagesAsync(
        GraphEmailIntegrationConfiguration email,
        InvoiceSearchCriteria criteria,
        string token,
        CancellationToken cancellationToken)
    {
        var windowStart = criteria.ExpectedDate.AddDays(-criteria.DateToleranceDays).ToDateTime(TimeOnly.MinValue);
        var windowEnd = criteria.ExpectedDate.AddDays(criteria.DateToleranceDays).ToDateTime(TimeOnly.MaxValue);

        var filter =
            $"receivedDateTime ge {windowStart:yyyy-MM-ddTHH:mm:ssZ} " +
            $"and receivedDateTime le {windowEnd:yyyy-MM-ddTHH:mm:ssZ} " +
            $"and from/emailAddress/address eq '{EscapeODataStringLiteral(email.SenderEmailAddress)}' " +
            $"and hasAttachments eq true";

        string? url =
            $"{GraphBaseUrl}/me/messages" +
            $"?$filter={Uri.EscapeDataString(filter)}" +
            $"&$select=id,receivedDateTime,body" +
            $"&$orderby=receivedDateTime";

        var messages = new List<GraphMessage>();
        while (url is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Plain-text body, not raw HTML, so a configured bodyPattern regex can match
            // clean text rather than markup — and not the 255-character bodyPreview.
            request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            await response.EnsureSuccessAsync("Microsoft Graph", "listing candidate emails", cancellationToken);

            var page = await response.Content.ReadFromJsonAsync<GraphMessageListResponse>(cancellationToken);
            foreach (var message in page?.Value ?? [])
            {
                if (MatchesBodyPattern(message, email.BodyPattern))
                    messages.Add(message);
            }

            url = page?.NextLink;
        }

        return messages;
    }

    /// <summary>
    /// Escapes a value for use inside a single-quoted OData string literal by doubling
    /// embedded single quotes (OData's own escaping rule) — <see cref="Uri.EscapeDataString"/>
    /// only percent-encodes for URL transport and does not do this.
    /// </summary>
    private static string EscapeODataStringLiteral(string value) => value.Replace("'", "''");

    /// <summary>
    /// <see cref="InvoiceFilename"/> reads the canonical OneDrive filename back as
    /// single-space-separated tokens, so a <c>SourceInvoiceId</c> containing whitespace
    /// (or empty) would fail to round-trip through <see cref="InvoiceFilename.TryParse"/>
    /// and break later reconciliation.
    /// </summary>
    private static bool IsValidSourceInvoiceId(string candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && !candidate.Any(char.IsWhiteSpace);

    private bool MatchesBodyPattern(GraphMessage message, string bodyPattern)
    {
        if (string.IsNullOrEmpty(bodyPattern))
            return true;

        var body = message.Body?.Content ?? string.Empty;
        try
        {
            return Regex.IsMatch(body, bodyPattern, RegexOptions.None, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogWarning("Body pattern match timed out for message {MessageId}; treating as no match.", message.Id);
            return false;
        }
    }

    private async Task<IReadOnlyList<GraphAttachment>> ListPdfAttachmentsAsync(
        string messageId, string token, CancellationToken cancellationToken)
    {
        string? url = $"{GraphBaseUrl}/me/messages/{messageId}/attachments" +
            "?$select=id,name,contentType,isInline,microsoft.graph.fileAttachment/contentBytes";

        var attachments = new List<GraphAttachment>();
        while (url is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await response.EnsureSuccessAsync("Microsoft Graph", "listing message attachments", cancellationToken);

            var page = await response.Content.ReadFromJsonAsync<GraphAttachmentListResponse>(cancellationToken);
            attachments.AddRange(page?.Value ?? []);
            url = page?.NextLink;
        }

        return attachments
            .Where(a => !a.IsInline && string.Equals(a.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<byte[]> GetAttachmentContentAsync(
        string messageId, GraphAttachment attachment, string token, CancellationToken cancellationToken)
    {
        // Small attachments are inlined as base64 on the listing call; larger ones omit
        // contentBytes and must be fetched separately via $value.
        if (attachment.ContentBytes is { Length: > 0 } base64)
            return Convert.FromBase64String(base64);

        var url = $"{GraphBaseUrl}/me/messages/{messageId}/attachments/{attachment.Id}/$value";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await response.EnsureSuccessAsync("Microsoft Graph", "downloading attachment content", cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>A PDF attachment extracted successfully and satisfied the search criteria.</summary>
    private sealed record EmailInvoiceFound(byte[] PdfContent, PdfExtractionSucceeded Extraction, string AttachmentName);

    /// <summary>Every PDF attachment extracted fine but none satisfied the date/amount criteria — the wrong invoice, not an error.</summary>
    private sealed record EmailInvoiceCriteriaMismatch;

    /// <summary>At least one PDF attachment could not be read at all — a technical failure.</summary>
    private sealed record EmailInvoiceExtractionFailed(string Reason);

    /// <summary>The outcome of trying every PDF attachment on one candidate message.</summary>
    private union EmailInvoiceOutcome(EmailInvoiceFound, EmailInvoiceCriteriaMismatch, EmailInvoiceExtractionFailed);

    private sealed record GraphMessageListResponse(
        [property: JsonPropertyName("value")] IReadOnlyList<GraphMessage>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

    private sealed record GraphMessage(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("receivedDateTime")] DateTimeOffset ReceivedDateTime,
        [property: JsonPropertyName("body")] GraphBody? Body);

    private sealed record GraphBody(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record GraphAttachmentListResponse(
        [property: JsonPropertyName("value")] IReadOnlyList<GraphAttachment>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

    private sealed record GraphAttachment(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("contentBytes")] string? ContentBytes,
        [property: JsonPropertyName("isInline")] bool IsInline);
}
