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
        // attachment is set via an ordinary PUT to the bill's own URL, nested under "bill".
        Assert.Equal(BillUrl, handler.Requests[1].RequestUri!.ToString());
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

    private static string BillWithAttachmentJson(string fileName, long fileSize) =>
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
              "content_type": "{{FreeAgentAttachmentContentType.Pdf}}",
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
