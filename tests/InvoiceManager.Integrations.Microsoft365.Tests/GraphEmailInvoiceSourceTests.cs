using System.Net;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations;
using InvoiceManager.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NodaMoney;

namespace InvoiceManager.Integrations.Microsoft365.Tests;

public sealed class GraphEmailInvoiceSourceTests
{
    private static readonly byte[] SinglePdfBytes = "%PDF-1.7 single"u8.ToArray();
    private static readonly byte[] SecondPdfBytes = "%PDF-1.7 second"u8.ToArray();

    private static InvoiceSearchCriteria Criteria(
        string bodyPattern = "", AmountMatchingCriteria? amountMatchingCriteria = null) => new(
        IntegrationConfiguration: new GraphEmailIntegrationConfiguration("billing@contoso.com", bodyPattern),
        ExpectedDate: new DateOnly(2025, 7, 10),
        DateToleranceDays: 5,
        AmountMatchingCriteria: amountMatchingCriteria is { } criteria ? criteria : Option.None);

    [Fact]
    public async Task FindInvoiceAsync_ReturnsMatch_ForSinglePdfAttachment()
    {
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))));
        var extractor = new FakePdfExtractor(_ => new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria());

        if (result is not InvoiceMatch match)
        {
            Assert.Fail($"Expected InvoiceMatch but got {result}.");
            return;
        }

        Assert.Equal(SinglePdfBytes, match.PdfContent);
        Assert.Equal(new DateOnly(2025, 7, 12), match.Details.ActualInvoiceDate);
        Assert.Equal(new Money(11.59m, "GBP"), match.Details.ActualAmount);
        Assert.Equal(new SourceInvoiceId("att-1"), match.Details.SourceInvoiceId);
    }

    [Fact]
    public async Task FindInvoiceAsync_ReturnsNoMatch_WhenNoCandidateEmails()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.ToString().Contains("/messages")
                ? Json(HttpStatusCode.OK, """{ "value": [] }""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = Build(handler, new FakePdfExtractor(_ => throw new InvalidOperationException("should not extract")));

        var result = await source.FindInvoiceAsync(Criteria());

        Assert.True(result is NoInvoiceMatch, $"Expected NoInvoiceMatch but got {result}.");
    }

    [Fact]
    public async Task FindInvoiceAsync_SkipsMessage_WhenBodyDoesNotMatchPattern()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.ToString().Contains("/messages")
                ? Json(HttpStatusCode.OK, Messages(("msg-1", "2025-07-12", "Unrelated newsletter content.")))
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = Build(handler, new FakePdfExtractor(_ => throw new InvalidOperationException("should not extract")));

        var result = await source.FindInvoiceAsync(Criteria(bodyPattern: "Invoice \\d+"));

        Assert.True(result is NoInvoiceMatch, $"Expected NoInvoiceMatch but got {result}.");
    }

    [Fact]
    public async Task FindInvoiceAsync_ReturnsNoMatch_WhenMatchedMessageHasNoPdfAttachment()
    {
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", """{ "value": [] }"""));
        var source = Build(handler, new FakePdfExtractor(_ => throw new InvalidOperationException("should not extract")));

        var result = await source.FindInvoiceAsync(Criteria());

        if (result is not NoInvoiceMatch noMatch)
        {
            Assert.Fail($"Expected NoInvoiceMatch but got {result}.");
            return;
        }

        // No amount was ever configured or even checked here (no PDF was found to check) -
        // the diagnostic must not claim otherwise (e.g. "expected amount any amount").
        Assert.DoesNotContain("amount", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindInvoiceAsync_TriesEachPdf_AndAcceptsTheOneThatExtracts()
    {
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes), ("att-2", SecondPdfBytes))));
        var extractor = new FakePdfExtractor(content =>
            content.SequenceEqual(SecondPdfBytes)
                ? new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(20m, "GBP"))
                : new PdfExtractionFailed("not an invoice"));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria());

        if (result is not InvoiceMatch match)
        {
            Assert.Fail($"Expected InvoiceMatch but got {result}.");
            return;
        }

        Assert.Equal(SecondPdfBytes, match.PdfContent);
    }

    [Fact]
    public async Task FindInvoiceAsync_Throws_WhenAllPdfAttachmentsFailExtraction()
    {
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))));
        var source = Build(handler, new FakePdfExtractor(_ => new PdfExtractionFailed("garbled")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FindInvoiceAsync(Criteria()));
    }

    [Fact]
    public async Task FindInvoiceAsync_ReturnsNoMatch_WhenExtractedAmountFailsAmountMatchingCriteria()
    {
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))));
        var extractor = new FakePdfExtractor(_ => new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(99.99m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria(
            amountMatchingCriteria: new AmountMatchingCriteria(new Money(11.59m, "GBP"), 0m)));

        if (result is not NoInvoiceMatch noMatch)
        {
            Assert.Fail($"Expected NoInvoiceMatch but got {result}.");
            return;
        }

        // The rejected PDF's actual date/amount must appear in the diagnostic - it is the
        // one piece of information a manual mailbox search can't quickly surface.
        Assert.Contains("2025-07-12", noMatch.Diagnostic);
        Assert.Contains("99.99", noMatch.Diagnostic);
        Assert.Contains("11.59", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindInvoiceAsync_DiagnosticBlamesDate_NotAmount_WhenExtractedInvoiceDateIsOutsideTolerance()
    {
        // The email's receivedDateTime is within the search window (so it's a candidate at
        // all), but the PDF's own extracted invoice date is not - a distinct rejection reason
        // from an amount mismatch, and one that must not be misreported as "amount" just
        // because no AmountMatchingCriteria happens to be configured.
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))));
        var extractor = new FakePdfExtractor(_ => new PdfExtractionSucceeded(new DateOnly(2025, 8, 1), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria());

        if (result is not NoInvoiceMatch noMatch)
        {
            Assert.Fail($"Expected NoInvoiceMatch but got {result}.");
            return;
        }

        Assert.Contains("2025-08-01", noMatch.Diagnostic);
        Assert.Contains("date tolerance", noMatch.Diagnostic);
        Assert.DoesNotContain("any amount", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindInvoiceAsync_DiagnosticAttributesReason_OnlyToNearestCandidate_WhenRejectionReasonsDiffer()
    {
        // Two rejected candidates fail for different reasons: the closer one (by extracted
        // invoice date) only fails on amount, the further one only fails on date. The
        // diagnostic must not generalize one candidate's rejection reason ("amount" or "date")
        // to the whole rejected set - only the nearest candidate's actual reason is reported.
        var handler = MessagesThenAttachments(
            Messages(
                ("msg-1", "2025-07-12", "Your invoice is attached."),
                ("msg-2", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))),
            ("msg-2", Attachments(("att-2", SecondPdfBytes))));
        var extractor = new FakePdfExtractor(content =>
            content.SequenceEqual(SinglePdfBytes)
                // Closest to ExpectedDate (2025-07-10): fails on amount only.
                ? new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(99.99m, "GBP"))
                // Far from ExpectedDate: fails on date only (amount happens to be correct).
                : new PdfExtractionSucceeded(new DateOnly(2025, 8, 1), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria(
            amountMatchingCriteria: new AmountMatchingCriteria(new Money(11.59m, "GBP"), 0m)));

        if (result is not NoInvoiceMatch noMatch)
        {
            Assert.Fail($"Expected NoInvoiceMatch but got {result}.");
            return;
        }

        // The nearest candidate (07-12, 99.99) failed on amount - that reason must be
        // reported, not the further candidate's date-tolerance failure.
        Assert.Contains("99.99", noMatch.Diagnostic);
        Assert.Contains("expected amount", noMatch.Diagnostic);
        Assert.DoesNotContain("date tolerance", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindInvoiceAsync_SkipsCandidateWithWrongAmount_AndAcceptsAFurtherCandidateThatMatches()
    {
        var handler = MessagesThenAttachments(
            Messages(
                ("msg-1", "2025-07-10", "Your invoice is attached."),
                ("msg-2", "2025-07-13", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))),
            ("msg-2", Attachments(("att-2", SecondPdfBytes))));
        var extractor = new FakePdfExtractor(content =>
            content.SequenceEqual(SinglePdfBytes)
                ? new PdfExtractionSucceeded(new DateOnly(2025, 7, 10), new Money(99.99m, "GBP"))
                : new PdfExtractionSucceeded(new DateOnly(2025, 7, 13), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria(
            amountMatchingCriteria: new AmountMatchingCriteria(new Money(11.59m, "GBP"), 0m)));

        if (result is not InvoiceMatch match)
        {
            Assert.Fail($"Expected InvoiceMatch but got {result}.");
            return;
        }

        Assert.Equal(new SourceInvoiceId("att-2"), match.Details.SourceInvoiceId);
    }

    [Fact]
    public async Task FindInvoiceAsync_TriesFurtherCandidate_WhenClosestCandidatesPdfCannotBeRead()
    {
        var handler = MessagesThenAttachments(
            Messages(
                ("msg-1", "2025-07-10", "Your invoice is attached."),
                ("msg-2", "2025-07-13", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))),
            ("msg-2", Attachments(("att-2", SecondPdfBytes))));
        var extractor = new FakePdfExtractor(content =>
            content.SequenceEqual(SinglePdfBytes)
                ? new PdfExtractionFailed("garbled")
                : new PdfExtractionSucceeded(new DateOnly(2025, 7, 13), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria());

        if (result is not InvoiceMatch match)
        {
            Assert.Fail($"Expected InvoiceMatch but got {result}.");
            return;
        }

        Assert.Equal(new SourceInvoiceId("att-2"), match.Details.SourceInvoiceId);
    }

    [Fact]
    public async Task FindInvoiceAsync_Throws_WhenEveryCandidatesPdfCannotBeRead()
    {
        var handler = MessagesThenAttachments(
            Messages(
                ("msg-1", "2025-07-10", "Your invoice is attached."),
                ("msg-2", "2025-07-13", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))),
            ("msg-2", Attachments(("att-2", SecondPdfBytes))));
        var source = Build(handler, new FakePdfExtractor(_ => new PdfExtractionFailed("garbled")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FindInvoiceAsync(Criteria()));
        Assert.Contains("msg-1", exception.Message);
        Assert.Contains("msg-2", exception.Message);
    }

    [Fact]
    public async Task FindInvoiceAsync_FollowsAttachmentListPagination_ToFindPdfOnASecondPage()
    {
        const string nextPageUrl = "https://graph.microsoft.com/v1.0/me/messages/msg-1/attachments-page-2";
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.Contains("/messages?"))
                return Json(HttpStatusCode.OK, Messages(("msg-1", "2025-07-12", "Your invoice is attached.")));

            if (uri == nextPageUrl)
                return Json(HttpStatusCode.OK, Attachments(("att-2", SinglePdfBytes)));

            if (uri.Contains("/messages/msg-1/attachments"))
            {
                return Json(HttpStatusCode.OK, $$"""
                    { "value": [], "@odata.nextLink": "{{nextPageUrl}}" }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var extractor = new FakePdfExtractor(_ => new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria());

        if (result is not InvoiceMatch match)
        {
            Assert.Fail($"Expected InvoiceMatch but got {result}.");
            return;
        }

        Assert.Equal(SinglePdfBytes, match.PdfContent);
    }

    [Fact]
    public async Task FindInvoiceAsync_EscapesEmbeddedSingleQuote_InSenderEmailAddressFilter()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.ToString().Contains("/messages")
                ? Json(HttpStatusCode.OK, """{ "value": [] }""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = Build(handler, new FakePdfExtractor(_ => throw new InvalidOperationException("should not extract")));

        var criteria = new InvoiceSearchCriteria(
            IntegrationConfiguration: new GraphEmailIntegrationConfiguration("o'brien@contoso.com", ""),
            ExpectedDate: new DateOnly(2025, 7, 10),
            DateToleranceDays: 5,
            AmountMatchingCriteria: Option.None);

        await source.FindInvoiceAsync(criteria);

        var request = Assert.Single(handler.Requests);
        var decodedFilter = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert.Contains("address eq 'o''brien@contoso.com'", decodedFilter);
    }

    [Fact]
    public async Task FindInvoiceAsync_RequestsAttachmentContentBytes_ViaTypeCastSelect()
    {
        // Attachments come back as the base attachment type unless contentBytes is
        // requested via the fileAttachment type-cast; a bare "contentBytes" in $select
        // is rejected by Graph with a 400. This regression-tests the fixed query so a
        // revert to the bare form fails loudly instead of silently reintroducing the 400.
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("att-1", SinglePdfBytes))));
        var extractor = new FakePdfExtractor(_ => new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        await source.FindInvoiceAsync(Criteria());

        var attachmentsRequest = Assert.Single(
            handler.Requests, r => r.RequestUri!.ToString().Contains("/messages/msg-1/attachments"));
        var decodedQuery = Uri.UnescapeDataString(attachmentsRequest.RequestUri!.Query);
        Assert.Contains("microsoft.graph.fileAttachment/contentBytes", decodedQuery);
    }

    [Fact]
    public async Task FindInvoiceAsync_TreatsAttachmentNameWithWhitespace_AsUnreadable_AndTriesFurtherCandidates()
    {
        var handler = MessagesThenAttachments(
            Messages(
                ("msg-1", "2025-07-10", "Your invoice is attached."),
                ("msg-2", "2025-07-13", "Your invoice is attached.")),
            ("msg-1", Attachments(("Invoice July", SinglePdfBytes))),
            ("msg-2", Attachments(("att-2", SecondPdfBytes))));
        var extractor = new FakePdfExtractor(_ => new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var result = await source.FindInvoiceAsync(Criteria());

        if (result is not InvoiceMatch match)
        {
            Assert.Fail($"Expected InvoiceMatch but got {result}.");
            return;
        }

        Assert.Equal(new SourceInvoiceId("att-2"), match.Details.SourceInvoiceId);
    }

    [Fact]
    public async Task FindInvoiceAsync_Throws_WhenOnlyMatchingAttachmentNameContainsWhitespace()
    {
        var handler = MessagesThenAttachments(
            Messages(("msg-1", "2025-07-12", "Your invoice is attached.")),
            ("msg-1", Attachments(("Invoice July", SinglePdfBytes))));
        var extractor = new FakePdfExtractor(_ => new PdfExtractionSucceeded(new DateOnly(2025, 7, 12), new Money(11.59m, "GBP")));
        var source = Build(handler, extractor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FindInvoiceAsync(Criteria()));
        Assert.Contains("whitespace", exception.Message);
    }

    [Fact]
    public async Task FindInvoiceAsync_SendsBearerToken_ForMailReadScope()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.ToString().Contains("/messages")
                ? Json(HttpStatusCode.OK, """{ "value": [] }""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var tokenProvider = new FakeMicrosoftTokenProvider("graph-token");
        var source = Build(handler, new FakePdfExtractor(_ => throw new InvalidOperationException()), tokenProvider);

        await source.FindInvoiceAsync(Criteria());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer graph-token", request.Authorization);
        var scopes = Assert.Single(tokenProvider.RequestedScopes);
        Assert.Contains("https://graph.microsoft.com/Mail.Read", scopes);
    }

    private static GraphEmailInvoiceSource Build(
        StubHttpMessageHandler handler, FakePdfExtractor extractor, FakeMicrosoftTokenProvider? tokenProvider = null)
    {
        var httpClient = new HttpClient(handler);
        return new GraphEmailInvoiceSource(
            httpClient, tokenProvider ?? new FakeMicrosoftTokenProvider(), extractor,
            NullLogger<GraphEmailInvoiceSource>.Instance);
    }

    /// <summary>Routes "/messages" list calls to <paramref name="messagesJson"/> and attachment calls per message id.</summary>
    private static StubHttpMessageHandler MessagesThenAttachments(
        string messagesJson, params (string MessageId, string AttachmentsJson)[] attachmentsByMessage)
    {
        var byMessage = attachmentsByMessage.ToDictionary(x => x.MessageId, x => x.AttachmentsJson);
        return new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.Contains("/messages?"))
                return Json(HttpStatusCode.OK, messagesJson);

            foreach (var (messageId, attachmentsJson) in attachmentsByMessage)
            {
                if (uri.Contains($"/messages/{messageId}/attachments"))
                    return Json(HttpStatusCode.OK, attachmentsJson);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static string Messages(params (string Id, string ReceivedDate, string Body)[] messages)
    {
        var items = messages.Select(m => $$"""
            {
              "id": "{{m.Id}}",
              "receivedDateTime": "{{m.ReceivedDate}}T09:00:00Z",
              "body": { "content": "{{m.Body}}" }
            }
            """);
        return $$"""{ "value": [{{string.Join(",", items)}}] }""";
    }

    private static string Attachments(params (string Id, byte[] Content)[] attachments)
    {
        var items = attachments.Select(a => $$"""
            {
              "id": "{{a.Id}}",
              "name": "{{a.Id}}.pdf",
              "contentType": "application/pdf",
              "contentBytes": "{{Convert.ToBase64String(a.Content)}}",
              "isInline": false
            }
            """);
        return $$"""{ "value": [{{string.Join(",", items)}}] }""";
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>An <see cref="IInvoicePdfExtractor"/> test double driven by content.</summary>
    private sealed class FakePdfExtractor(Func<byte[], PdfExtractionResult> resultFor) : IInvoicePdfExtractor
    {
        public Task<PdfExtractionResult> ExtractAsync(byte[] pdfContent, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultFor(pdfContent));
    }
}
