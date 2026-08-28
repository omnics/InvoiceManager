using InvoiceManager.Core.Repositories;

namespace InvoiceManager.Core;

/// <summary>
/// The three operator-facing groupings the AdminWeb home dashboard sorts the 12
/// <see cref="InvoiceWorkflowState"/> cases into, so an operator scans a handful of buckets
/// rather than every workflow state by name.
/// </summary>
public enum InvoiceSyncBucket
{
    /// <summary><see cref="SavedToOneDrive"/>, <see cref="ReconciledFromOneDrive"/>, <see cref="FreeAgentAttached"/> - terminal successes.</summary>
    Complete,

    /// <summary>
    /// <see cref="NotFound"/>, <see cref="RetrievalError"/>, <see cref="FreeAgentError"/>,
    /// <see cref="FreeAgentInterventionPending"/> - stuck or failed; won't self-resolve without
    /// either a retry succeeding or an administrator decision/resync.
    /// </summary>
    NeedsAttention,

    /// <summary>Everything else - still automatically progressing on a later run.</summary>
    InProgress,
}

/// <summary>
/// One row of the AdminWeb home dashboard: a single <see cref="InvoiceRecord"/> alongside the
/// configuration context needed to render it, sorted (by the caller) on <see cref="Date"/>
/// descending across every configuration.
/// </summary>
public sealed record InvoiceSyncRow(
    InvoiceConfigurationId ConfigurationId,
    IntegrationType IntegrationType,
    string InvoiceDescription,
    bool IsActive,
    DateOnly Date,
    bool IsActualDate,
    InvoiceWorkflowState State,
    InvoiceSyncBucket Bucket,
    Option<string> Diagnostic)
{
    public bool CanResync => InvoiceRecordResync.IsEligible(State);
    public bool ResyncRequiresConfirmation => InvoiceRecordResync.RequiresConfirmation(State);
}

/// <summary>
/// Builds the AdminWeb home dashboard's rows: for every configuration, its current record (in
/// whatever state) plus the last one that completed, only when the current record isn't itself
/// complete - see docs/design/issue-128-home-dashboard.png and issue #128 for why. A
/// configuration that has never had a record generated for it contributes no rows.
/// </summary>
public sealed class InvoiceSyncOverview(
    InvoiceConfigurationService configurationService,
    IInvoiceRecordRepository recordRepository)
{
    public async Task<IReadOnlyList<InvoiceSyncRow>> GetRowsAsync(CancellationToken cancellationToken = default)
    {
        var configurations = await configurationService.ListAsync(cancellationToken);

        var rows = new List<InvoiceSyncRow>();
        foreach (var stored in configurations)
        {
            var configuration = stored.Configuration;

            if (await recordRepository.GetMostRecentAsync(configuration.Id, cancellationToken) is not InvoiceRecord current)
                continue;

            rows.Add(ToRow(configuration, current));

            if (Bucket(current.State) != InvoiceSyncBucket.Complete &&
                await recordRepository.GetMostRecentCompletedAsync(configuration.Id, cancellationToken) is InvoiceRecord lastCompleted)
            {
                rows.Add(ToRow(configuration, lastCompleted));
            }
        }

        return rows.OrderByDescending(r => r.Date).ToList();
    }

    private static InvoiceSyncRow ToRow(InvoiceConfiguration configuration, InvoiceRecord record) =>
        new(
            configuration.Id,
            configuration.IntegrationType,
            configuration.InvoiceDescription,
            configuration.IsActive,
            ActualDate(record.State) ?? record.ExpectedDate,
            IsActualDate: ActualDate(record.State) is not null,
            record.State,
            Bucket(record.State),
            Diagnostic(record.State));

    private static DateOnly? ActualDate(InvoiceWorkflowState state) => state switch
    {
        Retrieved retrieved => retrieved.ActualDetails.ActualInvoiceDate,
        ReconciledFromOneDrive reconciled => reconciled.ActualDetails.ActualInvoiceDate,
        SavedToOneDrive saved => saved.ActualDetails.ActualInvoiceDate,
        FreeAgentMatchExpected matchExpected => matchExpected.ActualDetails.ActualInvoiceDate,
        FreeAgentBillMatched matched => matched.ActualDetails.ActualInvoiceDate,
        FreeAgentBillReconciled reconciledBill => reconciledBill.ActualDetails.ActualInvoiceDate,
        FreeAgentAttached attached => attached.ActualDetails.ActualInvoiceDate,
        FreeAgentInterventionPending pending => pending.ActualDetails.ActualInvoiceDate,
        FreeAgentError freeAgentError => freeAgentError.ActualDetails.ActualInvoiceDate,
        _ => null,
    };

    public static InvoiceSyncBucket Bucket(InvoiceWorkflowState state) => state switch
    {
        SavedToOneDrive or ReconciledFromOneDrive or FreeAgentAttached => InvoiceSyncBucket.Complete,
        NotFound or RetrievalError or FreeAgentError or FreeAgentInterventionPending => InvoiceSyncBucket.NeedsAttention,
        _ => InvoiceSyncBucket.InProgress,
    };

    private static Option<string> Diagnostic(InvoiceWorkflowState state) => state switch
    {
        Expected expected => expected.LastDiagnostic,
        NotFound notFound => notFound.Diagnostic,
        RetrievalError retrievalError => retrievalError.ErrorMessage,
        FreeAgentMatchExpected matchExpected => matchExpected.LastMatchDiagnostic,
        FreeAgentError freeAgentError => freeAgentError.ErrorMessage,
        FreeAgentInterventionPending => "Guess-removal intervention pending administrator decision",
        FreeAgentBillMatched => "Matched to a FreeAgent bill; awaiting reconciliation",
        FreeAgentBillReconciled => "FreeAgent bill reconciled; awaiting attachment",
        Retrieved => "Retrieved; awaiting save to OneDrive",
        _ => Option.None,
    };
}
