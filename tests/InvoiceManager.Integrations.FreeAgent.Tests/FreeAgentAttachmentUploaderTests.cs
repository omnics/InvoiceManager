using System.Text.Json;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.TestSupport;

namespace InvoiceManager.Integrations.FreeAgent.Tests;

public sealed class FreeAgentAttachmentUploaderTests
{
    private const string BillUrl = "https://api.sandbox.freeagent.com/v2/bills/1";

    [Fact]
    public async Task UploadAsync_SkipsUpload_WhenExistingAttachmentMatchesOwnLastKnownGoodUpload()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 1024)),
                _ => throw new InvalidOperationException("No upload call should have been made."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        var result = await uploader.UploadAsync(
            new FreeAgentBillIdentity(BillUrl),
            [1, 2, 3],
            "invoice.pdf",
            new FreeAgentAttachmentMetadata("invoice.pdf", 1024, FreeAgentAttachmentContentType.Pdf, DateTimeOffset.UtcNow));

        Assert.True(result is FreeAgentAttachmentAlreadyCorrect, $"Expected FreeAgentAttachmentAlreadyCorrect but got {result}.");
        Assert.Single(handler.Requests); // only the GET, no PUT
    }

    [Fact]
    public async Task UploadAsync_SkipsUpload_WhenNoOwnHistory_ButExistingAttachmentMatchesTheUpcomingUpload()
    {
        // No expectedExisting (e.g. the InvoiceRecord that made the original upload was deleted
        // or resynced) - but the bill's existing attachment already has the exact name/size this
        // call is about to upload for this invoice, which is accepted as proof it's already
        // correct rather than "someone else's" - see issue #133.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 1024)),
                _ => throw new InvalidOperationException("No upload call should have been made."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        var result = await uploader.UploadAsync(
            new FreeAgentBillIdentity(BillUrl), new byte[1024], "invoice.pdf", Option.None);

        Assert.True(result is FreeAgentAttachmentAlreadyCorrect, $"Expected FreeAgentAttachmentAlreadyCorrect but got {result}.");
        Assert.Single(handler.Requests); // only the GET, no PUT
    }

    [Theory]
    [InlineData("different-name.pdf", 1024)]
    [InlineData("invoice.pdf", 500)]
    public async Task UploadAsync_ReturnsUnexpectedExisting_WhenNoOwnHistory_AndOnlyOneOfNameOrSizeMatchesTheUpcomingUpload(
        string existingFileName, long existingFileSize)
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithAttachmentJson(existingFileName, existingFileSize)),
                _ => throw new InvalidOperationException("No upload call should have been made."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        var result = await uploader.UploadAsync(
            new FreeAgentBillIdentity(BillUrl), new byte[1024], "invoice.pdf", Option.None);

        Assert.True(result is FreeAgentAttachmentUnexpectedExisting, $"Expected FreeAgentAttachmentUnexpectedExisting but got {result}.");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UploadAsync_ReturnsUnexpectedExisting_WhenAttachmentDoesNotMatchOwnHistory_AndNeverUploads()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithAttachmentJson("someone-elses-file.pdf", 500)),
                _ => throw new InvalidOperationException("No upload call should have been made."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        var result = await uploader.UploadAsync(
            new FreeAgentBillIdentity(BillUrl), [1, 2, 3], "invoice.pdf", Option.None);

        Assert.True(result is FreeAgentAttachmentUnexpectedExisting, $"Expected FreeAgentAttachmentUnexpectedExisting but got {result}.");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UploadAsync_UploadsAndVerifies_WhenNoExistingAttachment()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithoutAttachmentJson()),
                1 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 3)),
                2 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 3)),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        var result = await uploader.UploadAsync(
            new FreeAgentBillIdentity(BillUrl), [1, 2, 3], "invoice.pdf", Option.None);

        Assert.True(result is FreeAgentAttachmentUploaded, $"Expected FreeAgentAttachmentUploaded but got {result}.");
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        // FreeAgent has no standalone "/attachment" sub-resource endpoint for bills - the
        // attachment is set via an ordinary PUT to the bill's own URL, nested under "bill",
        // the same as any other bill field. Asserted on both the URL and the payload's exact
        // nesting so this test would fail if either half of the fix regressed independently.
        Assert.Equal(BillUrl, handler.Requests[1].RequestUri!.ToString());
        using var uploadRequestBody = JsonDocument.Parse(handler.Requests[1].Body!);
        var attachment = uploadRequestBody.RootElement.GetProperty("bill").GetProperty("attachment");
        Assert.Equal("invoice.pdf", attachment.GetProperty("file_name").GetString());
        Assert.Equal(FreeAgentAttachmentContentType.Pdf, attachment.GetProperty("content_type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), attachment.GetProperty("data").GetString());
    }

    [Fact]
    public async Task UploadAsync_ReportsFileNameMismatch_WhenVerificationFails()
    {
        // The PUT response echoes exactly what was sent - only the final read-back GET
        // disagrees - so a passing test proves the diagnostic comes from that read-back,
        // not from re-describing the request we made.
        var result = await UploadWithVerifyResponseAsync(BillWithAttachmentJson("mangled-name.pdf", 3));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
        if (result is FreeAgentVerificationFailed failed)
        {
            Assert.Equal(
                "The uploaded attachment could not be verified after upload: " +
                "file name expected 'invoice.pdf' but was 'mangled-name.pdf'.",
                failed.Detail);
        }
    }

    [Fact]
    public async Task UploadAsync_ReportsFileSizeMismatch_WhenVerificationFails()
    {
        var result = await UploadWithVerifyResponseAsync(BillWithAttachmentJson("invoice.pdf", 999));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
        if (result is FreeAgentVerificationFailed failed)
        {
            Assert.Equal(
                "The uploaded attachment could not be verified after upload: " +
                "file size expected 3 but was 999.",
                failed.Detail);
        }
    }

    [Fact]
    public async Task UploadAsync_ReportsContentTypeMismatch_WhenVerificationFails()
    {
        // "image/png" (not "application/pdf") - FreeAgent normalizes "application/x-pdf" to
        // "application/pdf" on read (see FreeAgentAttachmentContentType.IsPdf), so that specific
        // value is a legitimate read-back, not a mismatch to report.
        var result = await UploadWithVerifyResponseAsync(BillWithAttachmentJson("invoice.pdf", 3, "image/png"));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
        if (result is FreeAgentVerificationFailed failed)
        {
            Assert.Equal(
                "The uploaded attachment could not be verified after upload: " +
                "content type expected 'application/x-pdf' but was 'image/png'.",
                failed.Detail);
        }
    }

    [Fact]
    public async Task UploadAsync_ReportsEveryMismatch_WhenMultipleFieldsDisagree()
    {
        var result = await UploadWithVerifyResponseAsync(
            BillWithAttachmentJson("mangled-name.pdf", 999, "image/png"));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
        if (result is FreeAgentVerificationFailed failed)
        {
            Assert.Equal(
                "The uploaded attachment could not be verified after upload: " +
                "file name expected 'invoice.pdf' but was 'mangled-name.pdf'; " +
                "file size expected 3 but was 999; " +
                "content type expected 'application/x-pdf' but was 'image/png'.",
                failed.Detail);
        }
    }

    [Fact]
    public async Task UploadAsync_TreatsApplicationPdf_AsAValidReadBackOfApplicationXPdf()
    {
        // Confirmed against the sandbox: FreeAgent requires "application/x-pdf" on write (see
        // UploadAsync_SendsFreeAgentAttachmentContentTypePdf_NotApplicationPdf) but always
        // reports the standard "application/pdf" back when the same attachment is read - not
        // documented anywhere, and not a real mismatch.
        var result = await UploadWithVerifyResponseAsync(BillWithAttachmentJson("invoice.pdf", 3, "application/pdf"));

        Assert.True(result is FreeAgentAttachmentUploaded, $"Expected FreeAgentAttachmentUploaded but got {result}.");
    }

    [Fact]
    public async Task UploadAsync_SkipsUpload_WhenExistingContentTypeIsTheReadBackApplicationPdf()
    {
        // expectedExisting.ContentType (as constructed by DueInvoiceProcessor after a previous
        // attempt) always carries the write-time "application/x-pdf" literal, never a read-back
        // value - the existing bill's real read-back content type ("application/pdf") must still
        // count as a match, or a record whose only prior failure was this exact normalization
        // would wrongly be treated as "someone else attached something" on retry.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 1024, "application/pdf")),
                _ => throw new InvalidOperationException("No upload call should have been made."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        var result = await uploader.UploadAsync(
            new FreeAgentBillIdentity(BillUrl),
            [1, 2, 3],
            "invoice.pdf",
            new FreeAgentAttachmentMetadata("invoice.pdf", 1024, FreeAgentAttachmentContentType.Pdf, DateTimeOffset.UtcNow));

        Assert.True(result is FreeAgentAttachmentAlreadyCorrect, $"Expected FreeAgentAttachmentAlreadyCorrect but got {result}.");
        Assert.Single(handler.Requests); // only the GET, no PUT
    }

    private static async Task<FreeAgentAttachmentResult> UploadWithVerifyResponseAsync(string verifyResponseJson)
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithoutAttachmentJson()),
                1 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 3)),
                2 => JsonResponse(verifyResponseJson),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        return await uploader.UploadAsync(new FreeAgentBillIdentity(BillUrl), [1, 2, 3], "invoice.pdf", Option.None);
    }

    [Fact]
    public async Task UploadAsync_SendsFreeAgentAttachmentContentTypePdf_NotApplicationPdf()
    {
        // FreeAgent's documented attachment content_type values are image/png, image/x-png,
        // image/jpeg, image/jpg, image/gif, and application/x-pdf - "application/pdf" (the
        // standard MIME type) is not among them and FreeAgent rejects it with 400 Bad Request.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillWithoutAttachmentJson()),
                1 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 3)),
                2 => JsonResponse(BillWithAttachmentJson("invoice.pdf", 3)),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var uploader = new FreeAgentAttachmentUploader(client);

        await uploader.UploadAsync(new FreeAgentBillIdentity(BillUrl), [1, 2, 3], "invoice.pdf", Option.None);

        // Asserted against a literal, not FreeAgentAttachmentContentType.Pdf itself - the point
        // of this test is to catch the constant regressing back to "application/pdf", which an
        // assertion built from the same constant could never detect.
        var uploadRequestBody = handler.Requests[1].Body;
        Assert.Contains("\"content_type\":\"application/x-pdf\"", uploadRequestBody);
    }

    private static string BillWithAttachmentJson(string fileName, long fileSize, string? contentType = null) =>
        $$"""
        {
          "bill": {
            "url": "{{BillUrl}}",
            "contact": "https://api.sandbox.freeagent.com/v2/contacts/1",
            "reference": "REF-1",
            "dated_on": "2026-08-01",
            "due_on": "2026-08-30",
            "currency": "GBP",
            "total_value": "121.00",
            "paid_value": "0.00",
            "due_value": "121.00",
            "status": "Open",
            "attachment": {
              "url": "{{BillUrl}}/attachment",
              "content_type": "{{contentType ?? FreeAgentAttachmentContentType.Pdf}}",
              "file_name": "{{fileName}}",
              "file_size": {{fileSize}}
            },
            "bill_items": []
          }
        }
        """;

    private static string BillWithoutAttachmentJson() =>
        $$"""
        {
          "bill": {
            "url": "{{BillUrl}}",
            "contact": "https://api.sandbox.freeagent.com/v2/contacts/1",
            "reference": "REF-1",
            "dated_on": "2026-08-01",
            "due_on": "2026-08-30",
            "currency": "GBP",
            "total_value": "121.00",
            "paid_value": "0.00",
            "due_value": "121.00",
            "status": "Open",
            "bill_items": []
          }
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
