using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Integrations.FreeAgent;

/// <summary>
/// Uploads/replaces a FreeAgent bill's attachment. Idempotent even across a lost
/// <see cref="InvoiceRecord"/> history (e.g. a deleted or resynced record): an existing
/// attachment is recognised as already correct either against the record's own
/// last-known-good upload, or - failing that - by matching the name/size of the file this
/// call is about to upload for this exact invoice (see issue #133), before deciding whether
/// to call the (replacing) upload endpoint at all.
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

            // Content type is checked via FreeAgentAttachmentContentType.IsPdf, not equality
            // against expected.ContentType - FreeAgent always reports "application/pdf" on read
            // regardless of the "application/x-pdf" it required on write, so expected.ContentType
            // (recorded at upload/attempt time) can legitimately differ from what a read-back
            // ever reports.
            var matchesOwnLastUpload =
                expectedExisting is FreeAgentAttachmentMetadata expected &&
                string.Equals(expected.FileName, existingMetadata.FileName, StringComparison.Ordinal) &&
                expected.FileSizeBytes == existingMetadata.FileSizeBytes &&
                FreeAgentAttachmentContentType.IsPdf(existingMetadata.ContentType);

            // Also accepted even with no recorded history (e.g. after the InvoiceRecord that
            // uploaded it was deleted or resynced): the generated file name deterministically
            // encodes the invoice's vendor/date/amount, so a name+size match against the file
            // this exact call is about to upload is strong evidence it's this invoice's own
            // attachment, not a coincidence - see issue #133.
            var matchesUpcomingUpload =
                string.Equals(fileName, existingMetadata.FileName, StringComparison.Ordinal) &&
                pdfContent.Length == existingMetadata.FileSizeBytes &&
                FreeAgentAttachmentContentType.IsPdf(existingMetadata.ContentType);

            if (matchesOwnLastUpload || matchesUpcomingUpload)
                return new FreeAgentAttachmentAlreadyCorrect(existingMetadata);

            // An attachment is present that doesn't match our own last-known-good upload (or
            // none recorded) and doesn't match the file we'd upload for this invoice either -
            // someone attached something else directly in FreeAgent. Do not touch it; surface
            // for manual investigation.
            return new FreeAgentAttachmentUnexpectedExisting(existingMetadata);
        }

        await client.PostAttachmentAsync(bill.Url.OriginalString, pdfContent, fileName, cancellationToken);

        // Verify by reading the bill back rather than trusting the upload response alone -
        // check every field that forms the persisted last-known-good metadata, not just the
        // filename, so a stale or truncated attachment under the right filename is caught. The
        // metadata persisted as this record's last-known-good upload also comes from this
        // verified read, never the upload response, in case that response itself is stale.
        var verify = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);
        if (verify.Attachment is not { } verifiedAttachment)
        {
            return new FreeAgentVerificationFailed(
                "The uploaded attachment could not be verified after upload: the bill has no attachment.");
        }

        // Named mismatches, not just a single "verification failed" - none of file name, size, or
        // content type are secret, so recording exactly what disagreed is safe and is the only way
        // to diagnose a provider-side transform (e.g. character mangling) without re-triggering.
        var mismatches = new List<string>();
        if (!string.Equals(verifiedAttachment.FileName, fileName, StringComparison.Ordinal))
            mismatches.Add($"file name expected '{fileName}' but was '{verifiedAttachment.FileName}'");
        if (verifiedAttachment.FileSize != pdfContent.Length)
            mismatches.Add($"file size expected {pdfContent.Length} but was {verifiedAttachment.FileSize}");
        if (!FreeAgentAttachmentContentType.IsPdf(verifiedAttachment.ContentType))
            mismatches.Add($"content type expected '{FreeAgentAttachmentContentType.Pdf}' but was '{verifiedAttachment.ContentType}'");

        if (mismatches.Count > 0)
        {
            return new FreeAgentVerificationFailed(
                $"The uploaded attachment could not be verified after upload: {string.Join("; ", mismatches)}.");
        }

        var newMetadata = verifiedAttachment.ToAttachmentMetadata();
        return new FreeAgentAttachmentUploaded(newMetadata);
    }
}
