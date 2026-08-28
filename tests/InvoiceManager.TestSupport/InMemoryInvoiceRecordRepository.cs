using InvoiceManager.Core;
using InvoiceManager.Core.Repositories;

namespace InvoiceManager.TestSupport;

/// <summary>
/// An in-memory invoice record store with the same semantics as the Cosmos
/// implementation: most-recent by expected date, create-if-not-exists by ID.
/// </summary>
public class InMemoryInvoiceRecordRepository : IInvoiceRecordRepository
{
    private readonly List<InvoiceRecord> store;

    public InMemoryInvoiceRecordRepository(params InvoiceRecord[] initial)
    {
        store = [.. initial];
    }

    public IReadOnlyList<InvoiceRecord> All => store;

    public virtual Task<Option<InvoiceRecord>> GetMostRecentAsync(
        InvoiceConfigurationId configurationId,
        CancellationToken cancellationToken = default)
    {
        var record = store
            .Where(r => r.ConfigurationId == configurationId)
            .OrderByDescending(r => r.ExpectedDate)
            .FirstOrDefault();

        Option<InvoiceRecord> result = record is not null ? record : Option.None;
        return Task.FromResult(result);
    }

    public virtual Task<Option<InvoiceRecord>> GetMostRecentCompletedAsync(
        InvoiceConfigurationId configurationId,
        CancellationToken cancellationToken = default)
    {
        var record = store
            .Where(r => r.ConfigurationId == configurationId
                && r.State is SavedToOneDrive or ReconciledFromOneDrive or FreeAgentAttached)
            .OrderByDescending(r => r.ExpectedDate)
            .FirstOrDefault();

        Option<InvoiceRecord> result = record is not null ? record : Option.None;
        return Task.FromResult(result);
    }

    public Task CreateIfNotExistsAsync(InvoiceRecord record, CancellationToken cancellationToken = default)
    {
        if (!store.Any(r => r.Id == record.Id))
            store.Add(record);
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<InvoiceRecord>> ListDueAsync(
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InvoiceRecord> due = store
            .Where(r =>
                r.State is Expected or RetrievalError or Retrieved or FreeAgentMatchExpected or FreeAgentError
                && r.ExpectedDate <= asOf)
            .ToList();
        return Task.FromResult(due);
    }

    public virtual Task ReplaceAsync(InvoiceRecord record, CancellationToken cancellationToken = default)
    {
        var index = store.FindIndex(r => r.Id == record.Id);
        if (index >= 0)
            store[index] = record;
        else
            store.Add(record);
        return Task.CompletedTask;
    }

}

/// <summary>
/// An <see cref="InMemoryInvoiceRecordRepository"/> that throws the given
/// exception when the specified configuration is read, for exercising
/// per-configuration failure handling.
/// </summary>
public sealed class ThrowingInvoiceRecordRepository(InvoiceConfigurationId failFor, Exception exception)
    : InMemoryInvoiceRecordRepository
{
    public override Task<Option<InvoiceRecord>> GetMostRecentAsync(
        InvoiceConfigurationId configurationId,
        CancellationToken cancellationToken = default) =>
        configurationId == failFor
            ? throw exception
            : base.GetMostRecentAsync(configurationId, cancellationToken);
}
