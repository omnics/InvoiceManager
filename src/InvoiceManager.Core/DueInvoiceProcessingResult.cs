namespace InvoiceManager.Core;

/// <summary>
/// The due invoice was retrieved and saved to OneDrive. If the configuration uses
/// FreeAgent, the bill was also matched, reconciled, and attached. Reaching this
/// state is a success state for <see cref="ExpectedRecordGenerator"/>'s purposes,
/// but that generator - not this processor - is what creates the next expected
/// record, on its next run.
/// </summary>
public sealed record ProcessingSucceeded(InvoiceRecordId RecordId);

/// <summary>
/// The due invoice was matched to a file already present in OneDrive: the record
/// was set to <see cref="ReconciledFromOneDrive"/>, without calling the source
/// integration or re-uploading. As with <see cref="ProcessingSucceeded"/>, the
/// next expected record is created by <see cref="ExpectedRecordGenerator"/> on
/// its next run, not by this processor.
/// </summary>
public sealed record ProcessingReconciled(InvoiceRecordId RecordId);

/// <summary>
/// No source-system invoice matched the due record, but it is still within its
/// tolerance window; the record is left <see cref="Expected"/> for a later run to
/// retry.
/// </summary>
public sealed record ProcessingNoMatch(InvoiceRecordId RecordId);

/// <summary>
/// No source-system invoice matched the due record and its tolerance window has
/// elapsed; the record is set to the terminal <see cref="NotFound"/> state.
/// </summary>
public sealed record ProcessingNotFound(InvoiceRecordId RecordId);

/// <summary>
/// Processing the due record failed with a technical error; the record is set to
/// <see cref="RetrievalError"/> for a later run to retry. Other records in the
/// same run are unaffected.
/// </summary>
public sealed record ProcessingFailed(InvoiceRecordId RecordId, Exception Exception);

/// <summary>More than one FreeAgent bill matched; the record stays at its last save/reconcile state for retry.</summary>
public sealed record ProcessingFreeAgentAmbiguous(InvoiceRecordId RecordId, int CandidateCount);

/// <summary>A FreeAgent Guess-removal intervention was created; the record is <see cref="FreeAgentInterventionPending"/>.</summary>
public sealed record ProcessingFreeAgentInterventionRequired(InvoiceRecordId RecordId, FreeAgentInterventionId InterventionId);

/// <summary>A FreeAgent step hit a lock, rejection, or verification failure; the record is <see cref="FreeAgentError"/> for retry.</summary>
public sealed record ProcessingFreeAgentConflict(InvoiceRecordId RecordId, string Reason);

/// <summary>The outcome of processing a single due invoice record.</summary>
public union DueInvoiceProcessingResult(
    ProcessingSucceeded,
    ProcessingReconciled,
    ProcessingNoMatch,
    ProcessingNotFound,
    ProcessingFailed,
    ProcessingFreeAgentAmbiguous,
    ProcessingFreeAgentInterventionRequired,
    ProcessingFreeAgentConflict);
