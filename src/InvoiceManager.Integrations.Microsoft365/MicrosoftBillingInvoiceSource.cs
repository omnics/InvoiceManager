using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations;
using InvoiceManager.Infrastructure.Http;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaMoney;

namespace InvoiceManager.Integrations.Microsoft365;

/// <summary>
/// Retrieves invoices from the Azure Billing REST API for Microsoft 365. Lists
/// invoices in the expected date window, matches on date and amount within the
/// configured tolerances, downloads the matched invoice document (handling the
/// async 202/Location download pattern), and returns PDF bytes — extracting the
/// single PDF from a ZIP where the API returns one.
/// </summary>
public sealed class MicrosoftBillingInvoiceSource(
    HttpClient httpClient,
    IMicrosoftTokenProvider tokenProvider,
    IOptions<MicrosoftBillingOptions> options,
    ILogger<MicrosoftBillingInvoiceSource> logger) : IInvoiceSourceIntegration
{
    private const string BillingBaseUrl = "https://management.azure.com/providers/Microsoft.Billing/billingAccounts";
    private const byte ZipMagic0 = 0x50; // 'P'
    private const byte ZipMagic1 = 0x4B; // 'K'

    private readonly MicrosoftBillingOptions settings = options.Value;

    public IntegrationType IntegrationType => IntegrationType.MicrosoftBilling;

    public async Task<InvoiceSourceResult> FindInvoiceAsync(
        InvoiceSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        if (criteria.IntegrationConfiguration is not MicrosoftBillingIntegrationConfiguration billing)
            throw new InvalidOperationException(
                $"{nameof(MicrosoftBillingInvoiceSource)} received criteria for an unsupported integration configuration.");

        using var activity = Telemetry.ActivitySource.StartActivity($"find_invoice.{IntegrationType.ToString().ToLowerInvariant()}");
        activity?.SetTag("invoice.billing_account_id", billing.BillingAccountId);
        activity?.SetTag("invoice.expected_date", criteria.ExpectedDate.ToString("O"));
        activity?.SetTag("invoice.date_tolerance_days", criteria.DateToleranceDays);

        var token = await tokenProvider.AcquireTokenAsync([settings.Scope], cancellationToken);

        var invoices = await ListInvoicesAsync(billing.BillingAccountId, criteria, token, cancellationToken);
        activity?.SetTag("invoice.candidate_count", invoices.Count);
        if (SelectBestMatch(invoices, criteria) is not BillingInvoice match)
        {
            var diagnostic = BuildNoMatchDiagnostic(invoices, criteria, billing.BillingAccountId);
            activity?.AddEvent(new ActivityEvent("no_match"));
            logger.LogInformation(
                "No {IntegrationType} invoice matched criteria for billing account {BillingAccountId} around {ExpectedDate}: {Diagnostic}",
                IntegrationType,
                billing.BillingAccountId, criteria.ExpectedDate, diagnostic);
            return new NoInvoiceMatch(diagnostic);
        }

        activity?.SetTag("invoice.matched_name", match.Name);
        activity?.AddEvent(new ActivityEvent("match_selected"));

        var document = match.Properties.Documents?.FirstOrDefault(d => d.Kind == "Invoice")
            ?? throw new InvalidOperationException(
                $"Microsoft 365 invoice '{match.Name}' matched but has no document of kind 'Invoice'.");

        var downloadUrl = await RequestDownloadUrlAsync(billing.BillingAccountId, match.Name, document.Name, token, cancellationToken);
        var payload = await DownloadAsync(downloadUrl, cancellationToken);
        var pdf = ExtractPdf(payload);
        logger.LogInformation(
            "Retrieved {IntegrationType} invoice {InvoiceName} ({PdfBytes} bytes) for billing account {BillingAccountId}.",
            IntegrationType,
            match.Name, pdf.Length, billing.BillingAccountId);

        var details = new ActualInvoiceDetails(
            DateOnly.FromDateTime(match.Properties.InvoiceDate.UtcDateTime),
            new Money(match.Properties.TotalAmount.Value, match.Properties.TotalAmount.Currency),
            new SourceInvoiceId(match.Name));

        return new InvoiceMatch(pdf, details);
    }

    private async Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(
        string billingAccountId,
        InvoiceSearchCriteria criteria,
        string token,
        CancellationToken cancellationToken)
    {
        // Azure only supports server-side filtering by invoice billing period; its
        // generic filter parameter rejects invoiceDate expressions. Fetch 14 months
        // of billing periods to cover monthly and annual invoices, then apply the
        // precise invoiceDate tolerance (and optional amount) locally.
        var periodStart = criteria.ExpectedDate.AddMonths(-14);
        var periodEnd = criteria.ExpectedDate.AddDays(criteria.DateToleranceDays);

        string? url =
            $"{BillingBaseUrl}/{Uri.EscapeDataString(billingAccountId)}/invoices" +
            $"?api-version={settings.ApiVersion}" +
            $"&periodStartDate={periodStart:yyyy-MM-dd}" +
            $"&periodEndDate={periodEnd:yyyy-MM-dd}";

        var invoices = new List<BillingInvoice>();
        while (url is not null)
        {
            using var response = await SendAuthenticatedAsync(HttpMethod.Get, url, token, content: null, cancellationToken);
            await response.EnsureSuccessAsync("Microsoft 365 billing", "listing invoices", cancellationToken);

            var page = await response.Content.ReadFromJsonAsync<BillingInvoiceListResponse>(cancellationToken);
            if (page is null)
                break;

            invoices.AddRange(page.Value);
            url = page.NextLink;
        }

        return invoices;
    }

    private static BillingInvoice? SelectBestMatch(IReadOnlyList<BillingInvoice> invoices, InvoiceSearchCriteria criteria) =>
        invoices
            .Where(invoice =>
            {
                var amount = invoice.Properties.TotalAmount;
                return criteria.Matches(
                    DateOnly.FromDateTime(invoice.Properties.InvoiceDate.UtcDateTime),
                    new Money(amount.Value, amount.Currency));
            })
            .OrderBy(invoice =>
                criteria.DateDistanceDays(DateOnly.FromDateTime(invoice.Properties.InvoiceDate.UtcDateTime)))
            .FirstOrDefault();

    /// <summary>
    /// Explains why <see cref="SelectBestMatch"/> found nothing: the date window
    /// searched, the expected amount/tolerance (if configured), and - if any
    /// invoices fell within the date window but were rejected on amount - the
    /// nearest one's actual date/amount, so a price change shows up directly in
    /// the diagnostic rather than requiring a manual provider lookup.
    /// </summary>
    private static string BuildNoMatchDiagnostic(
        IReadOnlyList<BillingInvoice> invoices, InvoiceSearchCriteria criteria, string billingAccountId)
    {
        var windowStart = criteria.ExpectedDate.AddDays(-criteria.DateToleranceDays);
        var windowEnd = criteria.ExpectedDate.AddDays(criteria.DateToleranceDays);
        var amountDescription = criteria.AmountMatchingCriteria is AmountMatchingCriteria amc
            ? $"{amc.Amount.Amount} {amc.Amount.Currency.Code} (tolerance {amc.AmountTolerance})"
            : "any amount";

        var inWindow = invoices
            .Select(invoice => new
            {
                Date = DateOnly.FromDateTime(invoice.Properties.InvoiceDate.UtcDateTime),
                Amount = invoice.Properties.TotalAmount,
                invoice.Name,
            })
            .Where(candidate => candidate.Date >= windowStart && candidate.Date <= windowEnd)
            .OrderBy(candidate => criteria.DateDistanceDays(candidate.Date))
            .ToList();

        if (inWindow.Count == 0)
        {
            return $"No invoice for billing account {billingAccountId} is dated within {criteria.DateToleranceDays} " +
                $"day(s) of {criteria.ExpectedDate:yyyy-MM-dd} ({windowStart:yyyy-MM-dd} to {windowEnd:yyyy-MM-dd}); " +
                $"{invoices.Count} invoice(s) considered in the wider search window. Expected amount: {amountDescription}.";
        }

        var nearest = inWindow[0];
        return $"{inWindow.Count} invoice(s) dated within tolerance ({windowStart:yyyy-MM-dd} to {windowEnd:yyyy-MM-dd}) " +
            $"but none matched the expected amount {amountDescription}; nearest was {nearest.Name} on {nearest.Date:yyyy-MM-dd} " +
            $"for {nearest.Amount.Value} {nearest.Amount.Currency}.";
    }

    private async Task<string> RequestDownloadUrlAsync(
        string billingAccountId,
        string invoiceName,
        string documentName,
        string token,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BillingBaseUrl}/{Uri.EscapeDataString(billingAccountId)}/downloadDocuments" +
            $"?api-version={settings.ApiVersion}";

        var requestBody = JsonSerializer.Serialize(new[] { new { invoiceName, documentName } });

        using var response = await SendAuthenticatedAsync(
            HttpMethod.Post, url, token,
            new StringContent(requestBody, Encoding.UTF8, "application/json"),
            cancellationToken);

        var completed = await FollowDownloadOperationAsync(response, token, cancellationToken);
        var result = JsonSerializer.Deserialize<DownloadDocumentsResponse>(completed)
            ?? throw new InvalidOperationException("The document download response could not be parsed.");

        return string.IsNullOrWhiteSpace(result.Url)
            ? throw new InvalidOperationException("The document download response did not include a URL.")
            : result.Url;
    }

    private async Task<string> FollowDownloadOperationAsync(
        HttpResponseMessage initialResponse,
        string token,
        CancellationToken cancellationToken)
    {
        var response = initialResponse;
        var deadline = DateTimeOffset.UtcNow + settings.MaxPollDuration;
        var pollCount = 0;

        while (true)
        {
            if (response.StatusCode == HttpStatusCode.OK)
                return await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                await response.EnsureSuccessAsync("Microsoft 365 billing", "requesting document download", cancellationToken);
            }

            var pollUrl = response.Headers.Location
                ?? throw new InvalidOperationException("A 202 Accepted download response did not include a Location header.");
            var delay = response.Headers.RetryAfter?.Delta ?? settings.PollInterval;

            if (DateTimeOffset.UtcNow + delay > deadline)
            {
                Activity.Current?.SetStatus(ActivityStatusCode.Error, "Download polling exceeded the maximum duration.");
                logger.LogWarning(
                    "Microsoft 365 document download did not complete within {MaxPollDuration} after {PollCount} poll(s); giving up.",
                    settings.MaxPollDuration, pollCount);
                throw new TimeoutException($"The document download did not complete within {settings.MaxPollDuration}.");
            }

            pollCount++;
            logger.LogDebug(
                "Microsoft 365 document download not ready (poll {PollCount}); retrying after {Delay}.",
                pollCount, delay);

            await Task.Delay(delay, cancellationToken);

            response = await SendAuthenticatedAsync(HttpMethod.Get, pollUrl.ToString(), token, content: null, cancellationToken);
        }
    }

    private async Task<byte[]> DownloadAsync(string sasUrl, CancellationToken cancellationToken)
    {
        // The SAS URL carries its own credential; it must not be sent the bearer token.
        using var request = new HttpRequestMessage(HttpMethod.Get, sasUrl);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await response.EnsureSuccessAsync("Microsoft 365 billing", "downloading the invoice file", cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static byte[] ExtractPdf(byte[] payload)
    {
        if (payload is not [ZipMagic0, ZipMagic1, ..])
            return payload;

        using var stream = new MemoryStream(payload);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var pdfEntry = archive.Entries.SingleOrDefault(e =>
            e.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The downloaded ZIP did not contain exactly one PDF entry.");

        using var entryStream = pdfEntry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpMethod method,
        string url,
        string token,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return httpClient.SendAsync(request, cancellationToken);
    }

}
