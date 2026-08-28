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
/// The configuration's most recent record is not in a state a stale snapshot can explain - see
/// <see cref="InvoiceRecordResync.IsEligible"/> for the full eligible set. A record already past
/// that point (for example <see cref="Retrieved"/>, <see cref="FreeAgentBillMatched"/>, or
/// <see cref="FreeAgentBillReconciled"/>) is resolved within the same processing run per
/// docs/workflow-states.md, so a stale snapshot doesn't meaningfully explain it being stuck there.
/// </summary>
public sealed record ResyncNotEligible(InvoiceRecordId RecordId, InvoiceWorkflowState State);

/// <summary>
/// The record is eligible, but resyncing it would supersede a pending administrator decision -
/// see <see cref="InvoiceRecordResync.RequiresConfirmation"/> - and the caller did not pass
/// <c>confirmed: true</c>. Checked against the same record instance about to be mutated (not a
/// caller's earlier, possibly stale read), so a record that changed state between an operator's
/// page load and this call is judged on what it actually is now, not what it was.
/// </summary>
public sealed record ResyncConfirmationRequired(InvoiceRecordId RecordId, InvoiceWorkflowState State);

/// <summary>The outcome of attempting to resync a configuration's most recent invoice record.</summary>
public union InvoiceRecordResyncResult(
    ResyncSucceeded, ResyncConfigurationNotFound, ResyncNoRecordExists, ResyncNotEligible, ResyncConfirmationRequired);

/// <summary>
/// Recovers a record stuck against a stale <see cref="InvoiceProcessingSnapshot"/> - most
/// commonly a <see cref="NotFound"/> record whose configured search criteria (for example an
/// exact expected amount) no longer matches reality after a permanent change such as a
/// subscription price rise, and an <see cref="InvoiceConfiguration"/> edit alone cannot fix
/// because the record already carries its own frozen copy of the search criteria. Re-derives
/// that copy from the current configuration and resets the record to <see cref="Expected"/> so
/// it is retried the next time its configuration is processed (skipped while that
/// configuration is inactive) - manual intervention, matching the recovery path
/// docs/domain-model.md already documents for a terminal <see cref="NotFound"/> record.
///
/// <para>
/// Eligibility covers every non-terminal state except the three that are resolved within the
/// same processing run they're reached in (<see cref="Retrieved"/>, <see cref="FreeAgentBillMatched"/>,
/// <see cref="FreeAgentBillReconciled"/>) - a stale snapshot doesn't meaningfully explain a record
/// stuck at one of those, since it barely persists there at rest. Resetting a record that's
/// already past retrieval/save (for example <see cref="FreeAgentMatchExpected"/> or
/// <see cref="FreeAgentError"/>) back to <see cref="Expected"/> relies on OneDrive reconciliation
/// being idempotent to fast-forward it past retrieval/save again on the next run.
/// </para>
/// </summary>
public sealed class InvoiceRecordResync(
    IInvoiceRecordRepository recordRepository,
    IInvoiceConfigurationRepository configurationRepository,
    IFreeAgentInterventionRepository interventionRepository,
    TimeProvider timeProvider,
    ILogger<InvoiceRecordResync> logger)
{
    /// <summary>Whether <paramref name="state"/> can be resynced - see this class's remarks.</summary>
    public static bool IsEligible(InvoiceWorkflowState state) =>
        state is Expected or RetrievalError or NotFound
            or FreeAgentError or FreeAgentMatchExpected or FreeAgentInterventionPending;

    /// <summary>
    /// Whether resyncing <paramref name="state"/> silently supersedes something an administrator
    /// would otherwise decide - currently just <see cref="FreeAgentInterventionPending"/>, whose
    /// pending <see cref="FreeAgentGuessIntervention"/> gets superseded rather than decided.
    /// </summary>
    public static bool RequiresConfirmation(InvoiceWorkflowState state) => state is FreeAgentInterventionPending;

    public async Task<InvoiceRecordResyncResult> ResyncMostRecentAsync(
        InvoiceConfigurationId configurationId,
        IntegrationType integrationType,
        InvoiceConfigurationActor actor,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        var configurationResult = await configurationRepository.GetAsync(configurationId, integrationType, cancellationToken);
        if (configurationResult is not StoredInvoiceConfiguration stored)
            return new ResyncConfigurationNotFound();

        var mostRecentResult = await recordRepository.GetMostRecentAsync(configurationId, cancellationToken);
        if (mostRecentResult is not InvoiceRecord record)
            return new ResyncNoRecordExists();

        if (!IsEligible(record.State))
            return new ResyncNotEligible(record.Id, record.State);

        // Checked against `record` (just read above), not a caller's earlier snapshot - closes the
        // window where a due run could advance the record into FreeAgentInterventionPending between
        // an operator's page load and this call, and see it superseded without ever having actually
        // been confirmed against.
        if (RequiresConfirmation(record.State) && !confirmed)
            return new ResyncConfirmationRequired(record.Id, record.State);

        if (record.State is FreeAgentInterventionPending pending)
        {
            var decisionResult = await interventionRepository.RecordDecisionAsync(
                new FreeAgentGuessInterventionDecision(
                    pending.InterventionId,
                    FreeAgentGuessInterventionStatus.Superseded,
                    actor.ObjectId,
                    actor.DisplayName,
                    timeProvider.GetUtcNow()),
                cancellationToken);
            if (decisionResult is FreeAgentInterventionAlreadyDecided)
            {
                // Someone else already decided (or superseded) it first - proceed anyway, since
                // the record itself, not the intervention, is the source of truth for the resync.
                logger.LogInformation(
                    "Intervention {InterventionId} for record {RecordId} was already decided before this resync could supersede it.",
                    pending.InterventionId, record.Id);
            }
        }

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
