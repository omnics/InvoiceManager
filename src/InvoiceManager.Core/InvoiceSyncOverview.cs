using InvoiceManager.Core.Integrations.FreeAgent;
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
/// descending across every configuration. <see cref="Date"/>, <see cref="IsActualDate"/>,
/// <see cref="Bucket"/>, and <see cref="Diagnostic"/> are all derived from <see cref="State"/>
/// (and <see cref="ExpectedDate"/>) rather than supplied independently, so a row combining a
/// state with a contradictory bucket/diagnostic/date is unrepresentable.
/// </summary>
public sealed record InvoiceSyncRow(
    InvoiceConfigurationId ConfigurationId,
    IntegrationType IntegrationType,
    string InvoiceDescription,
    bool IsActive,
    DateOnly ExpectedDate,
    InvoiceWorkflowState State,
    bool IsMostRecent)
{
    public DateOnly Date => InvoiceSyncOverview.ActualDate(State) is DateOnly actual ? actual : ExpectedDate;
    public bool IsActualDate => InvoiceSyncOverview.ActualDate(State) is DateOnly;
    public InvoiceSyncBucket Bucket => InvoiceSyncOverview.Bucket(State);
    public Option<string> Diagnostic => InvoiceSyncOverview.Diagnostic(State);

    /// <summary>Where the downloaded invoice file/folder live in OneDrive, for the row's "Open file"/"Open folder" actions.</summary>
    public Option<OneDriveDetails> OneDrive => InvoiceSyncOverview.OneDrive(State);

    /// <summary>The matched FreeAgent bill, for the row's "Open FreeAgent bill" action.</summary>
    public Option<FreeAgentBillIdentity> FreeAgentBill => InvoiceSyncOverview.FreeAgentBill(State);

    /// <summary>
    /// Whether this row's Resync action is offered. <see cref="InvoiceRecordResync.ResyncMostRecentAsync"/>
    /// always operates on a configuration's single most recent record regardless of which row's
    /// button was clicked, so - now that several non-complete rows can be shown for one
    /// configuration (see issue #135) - only <see cref="IsMostRecent"/>'s row may offer it; an
    /// older stuck row is shown for visibility only, not resyncable, to avoid a click on it
    /// silently mutating a different, more recent record instead.
    /// </summary>
    public bool CanResync => IsMostRecent && InvoiceRecordResync.IsEligible(State);
    public bool ResyncRequiresConfirmation => InvoiceRecordResync.RequiresConfirmation(State);

    /// <summary>
    /// The exact underlying state name (e.g. "RetrievalError"), for display alongside the
    /// coarser <see cref="Bucket"/> - <c>State.GetType().Name</c> would instead return the
    /// generated union wrapper's own type name ("InvoiceWorkflowState") for every row, not the
    /// case it currently holds.
    /// </summary>
    public string StateName => InvoiceSyncOverview.StateName(State);

    /// <summary>
    /// The name shown for this row's configuration - <see cref="InvoiceDescription"/> when set,
    /// otherwise <see cref="ConfigurationId"/>. Shared by the view and by column-header sorting so
    /// both agree on what "Configuration" sorts by.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(InvoiceDescription) ? ConfigurationId.Value : InvoiceDescription;
}

/// <summary>
/// Builds the AdminWeb home dashboard's rows: for every configuration, every record that hasn't
/// completed (which can be more than one - e.g. several periods stuck in
/// <see cref="FreeAgentMatchExpected"/>) plus the last one that did complete - see
/// docs/design/issue-128-home-dashboard.png and issue #128 for the original design, and issue #135
/// for why a single "current record" isn't enough to surface every stuck record. A configuration
/// that has never had a record generated for it contributes no rows.
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

            // ListNonCompleteAsync is ordered by expected date descending, so nonComplete[0] (if
            // any) is the most recent non-complete record. The configuration's single overall
            // most recent record - the one InvoiceRecordResync.ResyncMostRecentAsync would act on
            // - is whichever of that and the most recent completed record has the later expected
            // date; every record is one or the other, so this pair alone determines it without an
            // extra repository round-trip.
            var nonComplete = await recordRepository.ListNonCompleteAsync(configuration.Id, cancellationToken);
            var lastCompletedOption = await recordRepository.GetMostRecentCompletedAsync(configuration.Id, cancellationToken);
            var lastCompleted = lastCompletedOption is InvoiceRecord completed ? completed : null;
            var mostRecentNonComplete = nonComplete.Count > 0 ? nonComplete[0] : null;

            var mostRecentId = (mostRecentNonComplete, lastCompleted) switch
            {
                (null, null) => null,
                ({ } nc, null) => nc.Id,
                (null, { } c) => c.Id,
                ({ } nc, { } c) => nc.ExpectedDate >= c.ExpectedDate ? nc.Id : c.Id,
            };

            foreach (var record in nonComplete)
                rows.Add(ToRow(configuration, record, record.Id == mostRecentId));

            if (lastCompleted is not null)
                rows.Add(ToRow(configuration, lastCompleted, lastCompleted.Id == mostRecentId));
        }

        return rows.OrderByDescending(r => r.Date).ToList();
    }

    private static InvoiceSyncRow ToRow(InvoiceConfiguration configuration, InvoiceRecord record, bool isMostRecent) =>
        new(
            configuration.Id,
            configuration.IntegrationType,
            configuration.InvoiceDescription,
            configuration.IsActive,
            record.ExpectedDate,
            record.State,
            isMostRecent);

    internal static Option<DateOnly> ActualDate(InvoiceWorkflowState state) => state switch
    {
        Expected => Option.None,
        NotFound => Option.None,
        RetrievalError => Option.None,
        Retrieved retrieved => retrieved.ActualDetails.ActualInvoiceDate,
        ReconciledFromOneDrive reconciled => reconciled.ActualDetails.ActualInvoiceDate,
        SavedToOneDrive saved => saved.ActualDetails.ActualInvoiceDate,
        FreeAgentMatchExpected matchExpected => matchExpected.ActualDetails.ActualInvoiceDate,
        FreeAgentBillMatched matched => matched.ActualDetails.ActualInvoiceDate,
        FreeAgentBillReconciled reconciledBill => reconciledBill.ActualDetails.ActualInvoiceDate,
        FreeAgentAttached attached => attached.ActualDetails.ActualInvoiceDate,
        FreeAgentInterventionPending pending => pending.ActualDetails.ActualInvoiceDate,
        FreeAgentError freeAgentError => freeAgentError.ActualDetails.ActualInvoiceDate,
    };

    internal static string StateName(InvoiceWorkflowState state) => state switch
    {
        Expected => nameof(Expected),
        NotFound => nameof(NotFound),
        RetrievalError => nameof(RetrievalError),
        Retrieved => nameof(Retrieved),
        ReconciledFromOneDrive => nameof(ReconciledFromOneDrive),
        SavedToOneDrive => nameof(SavedToOneDrive),
        FreeAgentMatchExpected => nameof(FreeAgentMatchExpected),
        FreeAgentBillMatched => nameof(FreeAgentBillMatched),
        FreeAgentBillReconciled => nameof(FreeAgentBillReconciled),
        FreeAgentAttached => nameof(FreeAgentAttached),
        FreeAgentInterventionPending => nameof(FreeAgentInterventionPending),
        FreeAgentError => nameof(FreeAgentError),
    };

    public static InvoiceSyncBucket Bucket(InvoiceWorkflowState state) => state switch
    {
        SavedToOneDrive or ReconciledFromOneDrive or FreeAgentAttached => InvoiceSyncBucket.Complete,
        NotFound or RetrievalError or FreeAgentError or FreeAgentInterventionPending => InvoiceSyncBucket.NeedsAttention,
        Expected or Retrieved or FreeAgentMatchExpected or FreeAgentBillMatched or FreeAgentBillReconciled =>
            InvoiceSyncBucket.InProgress,
    };

    internal static Option<string> Diagnostic(InvoiceWorkflowState state) => state switch
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
        SavedToOneDrive => Option.None,
        ReconciledFromOneDrive => Option.None,
        FreeAgentAttached => Option.None,
    };

    internal static Option<OneDriveDetails> OneDrive(InvoiceWorkflowState state) => state switch
    {
        ReconciledFromOneDrive reconciled => reconciled.OneDriveDetails,
        SavedToOneDrive saved => saved.OneDriveDetails,
        FreeAgentMatchExpected matchExpected => matchExpected.OneDriveDetails,
        FreeAgentBillMatched matched => matched.OneDriveDetails,
        FreeAgentBillReconciled reconciledBill => reconciledBill.OneDriveDetails,
        FreeAgentAttached attached => attached.OneDriveDetails,
        FreeAgentInterventionPending pending => pending.OneDriveDetails,
        FreeAgentError freeAgentError => freeAgentError.OneDriveDetails,
        Expected or NotFound or RetrievalError or Retrieved => Option.None,
    };

    internal static Option<FreeAgentBillIdentity> FreeAgentBill(InvoiceWorkflowState state) => state switch
    {
        FreeAgentBillMatched matched => matched.Bill,
        FreeAgentBillReconciled reconciledBill => reconciledBill.Bill,
        FreeAgentAttached attached => attached.Bill,
        FreeAgentInterventionPending pending => pending.Bill,
        // Matching had already found a bill before this run errored - a lock, a rejection, or a
        // reconciliation failure all know exactly which bill they were acting on, worth
        // surfacing even though the record needs attention.
        FreeAgentError { BillContext: FreeAgentErrorBillContext context } => context.Bill,
        _ => Option.None,
    };
}
