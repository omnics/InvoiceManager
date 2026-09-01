namespace InvoiceManager.Core.Repositories;

/// <summary>
/// Persistent store for invoice records.
/// </summary>
public interface IInvoiceRecordRepository
{
    /// <summary>
    /// Returns the most recent invoice record for the given configuration (by expected date),
    /// or <see cref="Option.None"/> if no records exist.
    /// </summary>
    Task<Option<InvoiceRecord>> GetMostRecentAsync(InvoiceConfigurationId configurationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent invoice record (by expected date) for the given configuration
    /// whose state is a terminal success - <see cref="SavedToOneDrive"/>, <see cref="ReconciledFromOneDrive"/>,
    /// or <see cref="FreeAgentAttached"/> - or <see cref="Core.None"/> if the configuration has
    /// never completed a record. Used alongside <see cref="GetMostRecentAsync"/> to show "last
    /// completed" next to a current record that is still in progress or needs attention.
    /// </summary>
    Task<Option<InvoiceRecord>> GetMostRecentCompletedAsync(InvoiceConfigurationId configurationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every invoice record for the given configuration whose state is not a terminal
    /// success (i.e. everything <see cref="GetMostRecentCompletedAsync"/> would exclude), ordered
    /// by expected date descending. A configuration can accumulate more than one of these between
    /// its last completed record and its current one - for example several periods stuck in
    /// <see cref="FreeAgentMatchExpected"/> - so the AdminWeb home dashboard uses this instead of
    /// just the single most recent record to make sure none of them go unnoticed.
    /// </summary>
    Task<IReadOnlyList<InvoiceRecord>> ListNonCompleteAsync(InvoiceConfigurationId configurationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new invoice record. If a record with the same ID already exists (i.e. the same
    /// configuration and expected date), the call is a no-op — the existing record is not overwritten.
    /// </summary>
    Task CreateIfNotExistsAsync(InvoiceRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every record still awaiting processing or eligible for retry whose
    /// expected date is on or before <paramref name="asOf"/>, across all
    /// configurations. This includes records still awaiting retrieval or not yet
    /// available (<see cref="Expected"/>), records that failed a previous retrieval
    /// (<see cref="RetrievalError"/>), and records retrieved before a previous save
    /// attempt failed (<see cref="Retrieved"/>). The terminal <see cref="NotFound"/>
    /// state is excluded.
    /// </summary>
    Task<IReadOnlyList<InvoiceRecord>> ListDueAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites an existing invoice record with an updated state. Used to advance a record
    /// through its workflow (for example from <see cref="Expected"/> to <see cref="Retrieved"/>).
    /// </summary>
    Task ReplaceAsync(InvoiceRecord record, CancellationToken cancellationToken = default);
}
