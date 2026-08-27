using InvoiceManager.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace InvoiceManager.Core;

/// <summary>The resync refreshed the record's snapshot from the current configuration and reset it to <see cref="Expected"/>.</summary>
public sealed record ResyncSucceeded(InvoiceRecordId RecordId);

/// <summary>No configuration with the given ID/integration type exists.</summary>
public sealed record ResyncConfigurationNotFound;

/// <summary>The configuration has no record yet, so there is nothing to resync.</summary>
public sealed record ResyncNoRecordExists;

/// <summary>
/// The configuration's most recent record is not in a state a stale snapshot can explain -
/// only <see cref="Expected"/>, <see cref="RetrievalError"/>, and <see cref="NotFound"/> (the
/// states reached before or because a source match was never found) are eligible. A record
/// already past that point (for example <see cref="Retrieved"/>, or any FreeAgent-stage state)
/// found its match under the snapshot it has, so resyncing it would silently substitute
/// different criteria mid-flight rather than fix a genuinely stale search.
/// </summary>
public sealed record ResyncNotEligible(InvoiceRecordId RecordId, InvoiceWorkflowState State);

/// <summary>The outcome of attempting to resync a configuration's most recent invoice record.</summary>
public union InvoiceRecordResyncResult(ResyncSucceeded, ResyncConfigurationNotFound, ResyncNoRecordExists, ResyncNotEligible);

/// <summary>
/// Recovers a record stuck against a stale <see cref="InvoiceProcessingSnapshot"/> - most
/// commonly a <see cref="NotFound"/> record whose configured search criteria (for example an
/// exact expected amount) no longer matches reality after a permanent change such as a
/// subscription price rise, and an <see cref="InvoiceConfiguration"/> edit alone cannot fix
/// because the record already carries its own frozen copy of the search criteria. Re-derives
/// that copy from the current configuration and resets the record to <see cref="Expected"/> so
/// the next run retries it - manual intervention, matching the recovery path
/// docs/domain-model.md already documents for a terminal <see cref="NotFound"/> record.
/// </summary>
public sealed class InvoiceRecordResync(
    IInvoiceRecordRepository recordRepository,
    IInvoiceConfigurationRepository configurationRepository,
    ILogger<InvoiceRecordResync> logger)
{
    public async Task<InvoiceRecordResyncResult> ResyncMostRecentAsync(
        InvoiceConfigurationId configurationId,
        IntegrationType integrationType,
        CancellationToken cancellationToken = default)
    {
        var configurationResult = await configurationRepository.GetAsync(configurationId, integrationType, cancellationToken);
        if (configurationResult is not StoredInvoiceConfiguration stored)
            return new ResyncConfigurationNotFound();

        var mostRecentResult = await recordRepository.GetMostRecentAsync(configurationId, cancellationToken);
        if (mostRecentResult is not InvoiceRecord record)
            return new ResyncNoRecordExists();

        if (record.State is not (Expected or RetrievalError or NotFound))
            return new ResyncNotEligible(record.Id, record.State);

        var resynced = record with
        {
            ProcessingSnapshot = InvoiceProcessingSnapshot.FromConfiguration(stored.Configuration),
            State = new Expected(Option.None),
        };
        await recordRepository.ReplaceAsync(resynced, cancellationToken);
        logger.LogInformation(
            "Resynced record {RecordId} from the current configuration for {ConfigurationId} and reset it to Expected.",
            resynced.Id, configurationId);
        return new ResyncSucceeded(resynced.Id);
    }
}
