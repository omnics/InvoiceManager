using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Core;

/// <summary>
/// The invoice is expected and still due for retrieval. Covers both records that
/// have never been attempted and records whose retrieval attempts have so far
/// found no match while still inside the tolerance window; later runs retry.
/// </summary>
/// <param name="LastDiagnostic">
/// Why the most recent attempt found nothing - <see cref="Core.None"/> for a
/// record that has never been attempted yet. Updated on every no-match attempt,
/// not just the final one, so an administrator watching before the deadline can
/// see what the last poll actually found.
/// </param>
public sealed record Expected(Option<string> LastDiagnostic);

/// <summary>
/// The invoice could not be found on or after the configured tolerance deadline.
/// </summary>
/// <param name="Diagnostic">Why the deadline-time attempt found nothing.</param>
public sealed record NotFound(string Diagnostic);

/// <summary>
/// A retrieval attempt failed with a technical error, so the system could not
/// determine whether the invoice exists. <see cref="ErrorMessage"/> captures the
/// failure for diagnosis. Later runs always retry, with no retry limit.
/// </summary>
public sealed record RetrievalError(string ErrorMessage);

/// <summary>
/// The invoice has been found by an integration and its actual values read.
/// </summary>
public sealed record Retrieved(ActualInvoiceDetails ActualDetails);

/// <summary>
/// The expected invoice was matched to a file already present in OneDrive
/// before the source integration retrieved a new copy. <see cref="MatchReason"/>
/// records why the file was accepted and <see cref="ReconciledAt"/> when it
/// happened, preserving the reconciliation audit trail.
/// </summary>
public sealed record ReconciledFromOneDrive(
    ActualInvoiceDetails ActualDetails,
    OneDriveDetails OneDriveDetails,
    string MatchReason,
    DateTimeOffset ReconciledAt);

/// <summary>
/// The retrieved invoice file has been saved to its OneDrive destination. Terminal
/// for configurations with no <see cref="FreeAgentBillMatching"/> configured; when
/// FreeAgent matching is configured, the record goes straight to
/// <see cref="FreeAgentMatchExpected"/> instead of this state (see
/// <c>DueInvoiceProcessor</c>'s save_fork), so this state is only ever the final,
/// non-retryable one.
/// </summary>
public sealed record SavedToOneDrive(ActualInvoiceDetails ActualDetails, OneDriveDetails OneDriveDetails);

/// <summary>
/// The saved/reconciled invoice's configuration has FreeAgent matching configured,
/// but no bill has matched yet (or the match was ambiguous). The single entry point
/// into the FreeAgent stage from both the retrieval-and-save path and the OneDrive
/// reconciliation path - see docs/workflow-states.md. Retried indefinitely on later
/// runs (there is no FreeAgent-side deadline/give-up state, unlike
/// <see cref="Expected"/>/<see cref="NotFound"/>): a retry re-fetches the PDF bytes
/// from OneDrive via <see cref="OneDriveDetails"/> rather than persisting them.
/// </summary>
/// <param name="LastMatchDiagnostic">
/// Why the most recent FreeAgent bill search attempt found nothing/was
/// ambiguous - <see cref="Core.None"/> when this state was just entered fresh
/// (from retrieval/save or OneDrive reconciliation) and no match has been
/// attempted yet. Updated on every unsuccessful attempt so an administrator can
/// see why the record is stuck without re-querying FreeAgent.
/// </param>
public sealed record FreeAgentMatchExpected(
    ActualInvoiceDetails ActualDetails, OneDriveDetails OneDriveDetails, Option<string> LastMatchDiagnostic);

/// <summary>
/// The retrieved/reconciled invoice has been matched to a FreeAgent bill, but no
/// reconciliation or attachment has happened yet for this run.
/// </summary>
public sealed record FreeAgentBillMatched(
    ActualInvoiceDetails ActualDetails, OneDriveDetails OneDriveDetails, FreeAgentBillIdentity Bill);

/// <summary>
/// The FreeAgent bill's date and/or amount have been reconciled and verified. Only
/// the bill identity is persisted - the full verified snapshot is transient
/// workflow-step output (logged at the time), not durable state; a later step
/// re-reads the bill fresh rather than trusting a stored snapshot to still be current.
/// </summary>
public sealed record FreeAgentBillReconciled(
    ActualInvoiceDetails ActualDetails, OneDriveDetails OneDriveDetails, FreeAgentBillIdentity Bill);

/// <summary>
/// The invoice PDF has been uploaded/replaced on the matched FreeAgent bill and its
/// attachment metadata verified. Terminal success state for records whose
/// configuration uses FreeAgent.
/// </summary>
public sealed record FreeAgentAttached(
    ActualInvoiceDetails ActualDetails,
    OneDriveDetails OneDriveDetails,
    FreeAgentBillIdentity Bill,
    FreeAgentAttachmentMetadata Attachment);

/// <summary>
/// FreeAgent processing could not continue automatically and needs an
/// administrator decision (a Guess-removal intervention). The record stays here -
/// not retried automatically - until a decision is recorded against
/// <see cref="InterventionId"/> and a later run re-attempts reconciliation.
/// </summary>
public sealed record FreeAgentInterventionPending(
    ActualInvoiceDetails ActualDetails, OneDriveDetails OneDriveDetails, FreeAgentInterventionId InterventionId);

/// <summary>
/// Proof that this workflow genuinely POSTed <see cref="Attachment"/> to <see cref="Bill"/>.
/// Bill-bound because matching can find a different bill on a later retry (the contact's
/// bills changed, an earlier ambiguous match resolved differently) - proof from an earlier
/// bill must never be presented as evidence for a bill it was never uploaded to.
/// </summary>
public sealed record FreeAgentAttemptedAttachment(FreeAgentBillIdentity Bill, FreeAgentAttachmentMetadata Attachment);

/// <summary>
/// A FreeAgent step failed technically, hit a lock/conflict, or returned a
/// business-rule rejection that isn't a normal match/no-match outcome. Always
/// retried on a later run, mirroring <see cref="RetrievalError"/>: <c>ListDueAsync</c>
/// includes this state, and <c>DueInvoiceProcessor</c> resumes it directly by
/// re-downloading the PDF from OneDrive and re-running matching, reconciliation,
/// and attachment as one step, rather than restarting OneDrive reconciliation or
/// source retrieval from scratch.
/// </summary>
/// <param name="Bill">
/// The bill this error occurred against, if matching had already found one by the time it
/// struck - a lock, a business rejection, or a reconciliation failure all know exactly which
/// bill they were acting on even though none of them ever attempted (or completed) an upload.
/// <see cref="Core.None"/> only for a failure before matching ever found a bill (e.g. the
/// re-download that precedes a fresh match attempt).
/// </param>
/// <param name="AttemptedAttachment">
/// Proof of an attachment this run genuinely POSTed to FreeAgent before erroring - set only
/// when the upload itself succeeded (bound to the bill it was uploaded to), whether the
/// error is that its read-back verification failed, or a later technical failure (a
/// reconciliation/persistence exception, or a re-download failure resuming this same
/// FreeAgentError) that struck after the upload but didn't change what was already known.
/// A retry passes it back as <c>expectedExisting</c>, but only when the newly matched bill
/// is the same one it was uploaded to, so it recognises its own prior upload instead of
/// resuming with a fabricated identity. <see cref="Core.None"/> for every other error cause
/// (a lock, a business rejection, or a failure before any attach was ever attempted) - this
/// field carries no record-backed proof in that case, though the uploader may still separately
/// recognise a pre-existing attachment as correct by matching the file about to be uploaded
/// (see issue #133).
/// </param>
public sealed record FreeAgentError(
    ActualInvoiceDetails ActualDetails,
    OneDriveDetails OneDriveDetails,
    string ErrorMessage,
    Option<FreeAgentBillIdentity> Bill,
    Option<FreeAgentAttemptedAttachment> AttemptedAttachment);

/// <summary>
/// The current state of an invoice record as it moves through retrieval,
/// reconciliation, and save steps. Each case carries exactly the data valid
/// in that state, so a record cannot exist in a state without the values that
/// state requires.
/// </summary>
public union InvoiceWorkflowState(
    Expected,
    NotFound,
    RetrievalError,
    Retrieved,
    ReconciledFromOneDrive,
    SavedToOneDrive,
    FreeAgentMatchExpected,
    FreeAgentBillMatched,
    FreeAgentBillReconciled,
    FreeAgentAttached,
    FreeAgentInterventionPending,
    FreeAgentError);
