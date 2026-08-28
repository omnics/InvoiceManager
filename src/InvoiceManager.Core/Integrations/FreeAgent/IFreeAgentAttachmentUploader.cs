namespace InvoiceManager.Core.Integrations.FreeAgent;

/// <summary>A new attachment was uploaded; the bill previously had none.</summary>
public sealed record FreeAgentAttachmentUploaded(FreeAgentAttachmentMetadata New);

/// <summary>An existing attachment was replaced, per FreeAgent's proven single-attachment-per-bill behaviour.</summary>
public sealed record FreeAgentAttachmentReplaced(FreeAgentAttachmentMetadata Previous, FreeAgentAttachmentMetadata New);

/// <summary>
/// The existing attachment already matches what would have been uploaded (compared
/// against the record's own last-known-good upload, or against the file this call is about to
/// upload for this invoice - see issue #133). No upload call was made - this is what makes
/// retries idempotent even across a lost record history.
/// </summary>
public sealed record FreeAgentAttachmentAlreadyCorrect(FreeAgentAttachmentMetadata Existing);

/// <summary>
/// The bill already has an attachment that matches neither the file about to be
/// uploaded nor the record's own last-known-good upload - e.g. someone attached
/// something else directly in FreeAgent. Never silently replaced: surfaced for
/// manual investigation, the same as <see cref="FreeAgentBillLocked"/>.
/// </summary>
public sealed record FreeAgentAttachmentUnexpectedExisting(FreeAgentAttachmentMetadata Existing);

/// <summary>The outcome of uploading an invoice PDF to a FreeAgent bill.</summary>
public union FreeAgentAttachmentResult(
    FreeAgentAttachmentUploaded,
    FreeAgentAttachmentReplaced,
    FreeAgentAttachmentAlreadyCorrect,
    FreeAgentAttachmentUnexpectedExisting,
    FreeAgentBillLocked,
    FreeAgentVerificationFailed);

/// <summary>
/// Uploads the retrieved/reconciled invoice PDF to a matched FreeAgent bill and
/// verifies the resulting attachment metadata.
/// </summary>
public interface IFreeAgentAttachmentUploader
{
    /// <param name="expectedExisting">
    /// The record's own last-known-good attachment metadata, if any, used to decide
    /// whether an existing attachment is already correct (skip), absent (fresh
    /// upload), or unexpected (do not touch). When absent or non-matching, an existing
    /// attachment can still be recognised as correct by matching the name/size of the
    /// file this call is about to upload for this invoice - see issue #133.
    /// </param>
    Task<FreeAgentAttachmentResult> UploadAsync(
        FreeAgentBillIdentity bill,
        byte[] pdfContent,
        string fileName,
        Option<FreeAgentAttachmentMetadata> expectedExisting,
        CancellationToken cancellationToken = default);
}
