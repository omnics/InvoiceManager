using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Integrations.FreeAgent;

/// <summary>
/// Uploads/replaces a FreeAgent bill's attachment. Idempotent: compares the
/// bill's existing attachment against the record's own last-known-good upload
/// before deciding whether to call the (replacing) upload endpoint at all.
/// </summary>
internal sealed class FreeAgentAttachmentUploader : IFreeAgentAttachmentUploader
{
    private readonly FreeAgentApiClient client;

    public FreeAgentAttachmentUploader(FreeAgentApiClient client)
    {
        this.client = client;
    }

    public async Task<FreeAgentAttachmentResult> UploadAsync(
        FreeAgentBillIdentity bill,
        byte[] pdfContent,
        string fileName,
        Option<FreeAgentAttachmentMetadata> expectedExisting,
        CancellationToken cancellationToken = default)
    {
        var current = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);
        var existingAttachment = current.Attachment;

        if (existingAttachment is not null)
        {
            var existingMetadata = existingAttachment.ToAttachmentMetadata();

            var matchesOwnLastUpload =
                expectedExisting is FreeAgentAttachmentMetadata expected &&
                string.Equals(expected.FileName, existingMetadata.FileName, StringComparison.Ordinal) &&
                expected.FileSizeBytes == existingMetadata.FileSizeBytes &&
                string.Equals(expected.ContentType, existingMetadata.ContentType, StringComparison.OrdinalIgnoreCase);

            if (matchesOwnLastUpload)
                return new FreeAgentAttachmentAlreadyCorrect(existingMetadata);

            // An attachment is present that doesn't match our own last-known-good upload
            // (or we have none recorded) - someone attached something else directly in
            // FreeAgent. Do not touch it; surface for manual investigation.
            return new FreeAgentAttachmentUnexpectedExisting(existingMetadata);
        }

        await client.PostAttachmentAsync(bill.Url.OriginalString, pdfContent, fileName, cancellationToken);

        // Verify by reading the bill back rather than trusting the upload response alone -
        // check every field that forms the persisted last-known-good metadata, not just the
        // filename, so a stale or truncated attachment under the right filename is caught. The
        // metadata persisted as this record's last-known-good upload also comes from this
        // verified read, never the upload response, in case that response itself is stale.
        var verify = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);
        if (verify.Attachment is not { } verifiedAttachment ||
            !string.Equals(verifiedAttachment.FileName, fileName, StringComparison.Ordinal) ||
            verifiedAttachment.FileSize != pdfContent.Length ||
            !string.Equals(verifiedAttachment.ContentType, FreeAgentAttachmentContentType.Pdf, StringComparison.OrdinalIgnoreCase))
        {
            return new FreeAgentVerificationFailed("The uploaded attachment could not be verified after upload.");
        }

        var newMetadata = verifiedAttachment.ToAttachmentMetadata();
        return new FreeAgentAttachmentUploaded(newMetadata);
    }
}
