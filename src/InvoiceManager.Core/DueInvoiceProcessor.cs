using System.Diagnostics;
using InvoiceManager.Core.Integrations;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace InvoiceManager.Core;

/// <summary>
/// Processes invoice records that are due for retrieval: for each due record it
/// asks the matching source integration for the invoice and saves it to OneDrive.
/// Only records whose configuration is active are considered (see
/// <see cref="ProcessDueAsync"/>). Records that are not yet available stay
/// <see cref="Expected"/> (retried on a later run of the same active
/// configuration) until their tolerance window elapses, after which they move to
/// the terminal <see cref="NotFound"/>. A technical failure moves the record to
/// <see cref="RetrievalError"/> (retried the same way) and is isolated so the
/// other records still run. State is persisted after each step so a later run
/// can continue without repeating completed work. Structured telemetry is
/// emitted per record and as a run summary.
/// </summary>
/// <remarks>
/// Does not create the next expected record itself - <see cref="ExpectedRecordGenerator"/>
/// does that, and is always run immediately before this processor in both
/// Functions entry points (<c>GenerateExpectedRecordsTimer</c>/<c>GenerateExpectedRecordsHttp</c>).
/// A record reaching a success state this run (see <see cref="NextExpectedInvoiceDate"/>)
/// is picked up by that generator on its <em>next</em> invocation, not this one -
/// generation is idempotent per period, so there is no risk of missing or
/// duplicating a record, only a delay of up to one processing cycle. This also
/// means an <see cref="InvoiceConfiguration"/> update made during this run (for
/// example an amount-tolerance auto-correction) only needs to be durably
/// persisted before that next invocation reloads configurations - no need to
/// thread an updated in-memory configuration through the rest of this run.
/// </remarks>
public sealed class DueInvoiceProcessor(
    IInvoiceRecordRepository recordRepository,
    IInvoiceConfigurationRepository configurationRepository,
    IEnumerable<IInvoiceSourceIntegration> sourceIntegrations,
    IOneDriveIntegration oneDriveIntegration,
    InvoiceFilename invoiceFilename,
    IFreeAgentBillMatcher freeAgentBillMatcher,
    IFreeAgentBillReconciler freeAgentBillReconciler,
    IFreeAgentAttachmentUploader freeAgentAttachmentUploader,
    IFreeAgentInterventionRepository freeAgentInterventionRepository,
    TimeProvider timeProvider,
    ILogger<DueInvoiceProcessor> logger)
{
    private readonly IReadOnlyDictionary<IntegrationType, IInvoiceSourceIntegration> sourcesByType =
        sourceIntegrations.ToDictionary(integration => integration.IntegrationType);

    /// <summary>
    /// Processes every due record (expected date on or before today, still
    /// awaiting retrieval or a retryable save). Returns a per-record outcome for
    /// each record processed.
    /// </summary>
    public async Task<IReadOnlyList<DueInvoiceProcessingResult>> ProcessDueAsync(
        CancellationToken cancellationToken = default)
    {
        var asOf = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        using var runActivity = Telemetry.ActivitySource.StartActivity("process_due_invoices");
        runActivity?.SetTag("invoice.as_of", asOf.ToString("O"));

        var configurations = await configurationRepository.ListActiveAsync(cancellationToken);
        var configurationsById = configurations.ToDictionary(configuration => configuration.Id);

        var dueRecords = await recordRepository.ListDueAsync(asOf, cancellationToken);
        var results = new List<DueInvoiceProcessingResult>(dueRecords.Count);

        runActivity?.SetTag("invoice.due_count", dueRecords.Count);
        logger.LogInformation("Due invoice processing run started for {DueRecordCount} record(s) as of {AsOf}.", dueRecords.Count, asOf);

        var skippedCount = 0;
        foreach (var record in dueRecords)
        {
            // Skip records whose configuration is no longer active or present: nothing
            // further can be done for them this run, so record why and move on.
            if (!configurationsById.TryGetValue(record.ConfigurationId, out _))
            {
                skippedCount++;
                runActivity?.AddEvent(new ActivityEvent("record_skipped_inactive_configuration",
                    tags: new ActivityTagsCollection
                    {
                        ["invoice.record_id"] = record.Id.Value,
                        ["invoice.configuration_id"] = record.ConfigurationId.Value,
                    }));
                logger.LogInformation(
                    "Skipping due record {RecordId}: configuration {ConfigurationId} is no longer active or present; no action taken.",
                    record.Id, record.ConfigurationId);
                continue;
            }

            using var recordActivity = Telemetry.ActivitySource.StartActivity("process_invoice_record");
            recordActivity?.SetTag("invoice.record_id", record.Id.Value);
            recordActivity?.SetTag("invoice.configuration_id", record.ConfigurationId.Value);
            var snapshot = record.ProcessingSnapshot;
            recordActivity?.SetTag("invoice.integration_type", snapshot.IntegrationType.ToString());
            recordActivity?.SetTag("invoice.description", snapshot.InvoiceDescription);
            recordActivity?.SetTag("invoice.expected_date", record.ExpectedDate.ToString("O"));

            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["RecordId"] = record.Id.Value,
                ["ConfigurationId"] = record.ConfigurationId.Value,
                ["IntegrationType"] = snapshot.IntegrationType.ToString(),
                ["InvoiceDescription"] = snapshot.InvoiceDescription,
                ["ExpectedDate"] = record.ExpectedDate,
            });

            try
            {
                var result = await ProcessAsync(record, snapshot, asOf, recordActivity, cancellationToken);
                recordActivity?.SetTag("invoice.outcome", OutcomeName(result));
                results.Add(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failure outside retrieval (for example a save step) leaves the record in
                // its last persisted, already-retryable state. Report it without stopping the
                // other records.
                recordActivity?.SetTag("invoice.outcome", "failed");
                recordActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                recordActivity?.AddException(ex);
                logger.LogError(ex, "Processing failed for invoice record {RecordId}.", record.Id);
                results.Add(new ProcessingFailed(record.Id, ex));
            }
        }

        runActivity?.SetTag("invoice.skipped_count", skippedCount);
        SetRunSummaryTags(runActivity, results);
        LogRunSummary(results);
        return results;
    }

    private static string OutcomeName(DueInvoiceProcessingResult result) => result switch
    {
        ProcessingSucceeded => "saved",
        ProcessingReconciled => "reconciled",
        ProcessingNoMatch => "no_match",
        ProcessingNotFound => "not_found",
        ProcessingFailed => "failed",
        ProcessingFreeAgentAmbiguous => "freeagent_ambiguous",
        ProcessingFreeAgentInterventionRequired => "freeagent_intervention_required",
        ProcessingFreeAgentConflict => "freeagent_conflict",
    };

    private async Task<DueInvoiceProcessingResult> ProcessAsync(
        InvoiceRecord record,
        InvoiceProcessingSnapshot snapshot,
        DateOnly asOf,
        Activity? recordActivity,
        CancellationToken cancellationToken)
    {
        // A record already inside the FreeAgent stage resumes there directly, re-fetching
        // the PDF bytes from OneDrive rather than restarting retrieval/reconciliation - see
        // docs/workflow-states.md's FreeAgentMatchExpected note.
        if (record.State is FreeAgentMatchExpected or FreeAgentError)
            return await ResumeFreeAgentStageAsync(record, snapshot, recordActivity, cancellationToken);

        if (!sourcesByType.TryGetValue(snapshot.IntegrationType, out var source))
        {
            throw new InvalidOperationException(
                $"No invoice source integration is registered for integration type '{snapshot.IntegrationType}'.");
        }

        var criteria = new InvoiceSearchCriteria(
            snapshot.IntegrationConfiguration,
            record.ExpectedDate,
            snapshot.DateToleranceDays,
            snapshot.AmountMatchingCriteria);

        // Reconcile first: a file already in OneDrive (a manual download or an
        // earlier partial run) is used as-is, skipping the source call and upload.
        // The description is part of the match so records for different subscriptions
        // sharing one folder do not reconcile against each other's files.
        var oneDriveCriteria = new OneDriveSearchCriteria(
            record.ExpectedDate,
            snapshot.DateToleranceDays,
            snapshot.AmountMatchingCriteria,
            snapshot.InvoiceDescription);

        OneDriveSearchResult search;
        try
        {
            search = await oneDriveIntegration.SearchAsync(
                new OneDriveSearchRequest(snapshot.OneDriveFolder, oneDriveCriteria), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await MarkRetrievalErrorAsync(
                record, ex, recordActivity, "OneDrive reconciliation search", cancellationToken);
        }

        if (search is OneDriveMatch reconciledMatch)
            return await ReconcileAsync(record, snapshot, reconciledMatch, recordActivity, cancellationToken);

        // No existing file: fall through to the source integration.
        InvoiceSourceResult result;
        try
        {
            result = await source.FindInvoiceAsync(criteria, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A technical failure during retrieval: the system cannot tell whether the
            // invoice exists. Record RetrievalError (always retryable) and move on.
            return await MarkRetrievalErrorAsync(record, ex, recordActivity, "Retrieval", cancellationToken);
        }

        if (result is not InvoiceMatch match)
        {
            var diagnostic = result is NoInvoiceMatch noMatch ? noMatch.Diagnostic : string.Empty;
            return await RecordNoMatchAsync(record, asOf, diagnostic, recordActivity, cancellationToken);
        }

        // Retrieved: persist before saving so a later run resumes from here.
        var retrieved = record with { State = new Retrieved(match.Details) };
        await recordRepository.ReplaceAsync(retrieved, cancellationToken);
        recordActivity?.AddEvent(new ActivityEvent("state_retrieved"));
        logger.LogInformation(
            "Invoice {SourceInvoiceId} retrieved for record {RecordId}; marked Retrieved before saving.",
            match.Details.SourceInvoiceId.Value, record.Id);

        var fileName = invoiceFilename.Generate(
            match.Details.ActualInvoiceDate,
            snapshot.InvoiceDescription,
            match.Details.SourceInvoiceId.Value,
            match.Details.ActualAmount,
            snapshot.VatMode);

        var oneDriveDetails = await oneDriveIntegration.UploadAsync(
            new OneDriveUploadRequest(snapshot.OneDriveFolder, fileName, match.PdfContent),
            cancellationToken);

        // save_fork: when FreeAgent matching is configured, go straight to
        // FreeAgentMatchExpected - SavedToOneDrive is never written - so a crash between
        // "file available" and "FreeAgent stage entered" never strands the record in a
        // state the due query has stopped re-selecting (see docs/workflow-states.md).
        if (snapshot.FreeAgentMatching is FreeAgentBillMatching matching)
        {
            var matchExpected = retrieved with
            {
                State = new FreeAgentMatchExpected(match.Details, oneDriveDetails, Option.None),
            };
            await recordRepository.ReplaceAsync(matchExpected, cancellationToken);
            recordActivity?.AddEvent(new ActivityEvent("state_freeagent_match_expected"));

            logger.LogInformation(
                "Saved invoice {FileName} for record {RecordId}; entering the FreeAgent stage.", fileName, record.Id);

            return await ProcessFreeAgentStageAsync(
                matchExpected, matching, match.Details, oneDriveDetails, match.PdfContent, fileName, recordActivity, cancellationToken);
        }

        var saved = retrieved with { State = new SavedToOneDrive(match.Details, oneDriveDetails) };
        await recordRepository.ReplaceAsync(saved, cancellationToken);
        recordActivity?.AddEvent(new ActivityEvent("state_saved_to_onedrive"));

        logger.LogInformation("Saved invoice {FileName} for record {RecordId}.", fileName, record.Id);

        return new ProcessingSucceeded(record.Id);
    }

    /// <summary>
    /// Resumes the FreeAgent stage for a record already at <see cref="FreeAgentMatchExpected"/>
    /// or <see cref="FreeAgentError"/>: re-downloads the invoice PDF from OneDrive (the bytes are
    /// never persisted between steps) and re-runs matching, reconciliation, and attachment as one
    /// step, exactly as <see cref="ProcessFreeAgentStageAsync"/> does within the initial run.
    /// </summary>
    private async Task<DueInvoiceProcessingResult> ResumeFreeAgentStageAsync(
        InvoiceRecord record,
        InvoiceProcessingSnapshot snapshot,
        Activity? recordActivity,
        CancellationToken cancellationToken)
    {
        (ActualInvoiceDetails actualDetails, OneDriveDetails oneDriveDetails, Option<FreeAgentAttachmentMetadata> existingAttemptedAttachment) = record.State switch
        {
            FreeAgentMatchExpected matchExpected => (matchExpected.ActualDetails, matchExpected.OneDriveDetails, Option.None),
            FreeAgentError { BillContext: FreeAgentErrorBillContext context } error => (error.ActualDetails, error.OneDriveDetails, context.AttemptedAttachment),
            FreeAgentError error => (error.ActualDetails, error.OneDriveDetails, Option.None),
            _ => throw new InvalidOperationException(
                $"ResumeFreeAgentStageAsync called for record {record.Id} in unsupported state '{record.State.GetType().Name}'."),
        };

        if (snapshot.FreeAgentMatching is not FreeAgentBillMatching matching)
        {
            throw new InvalidOperationException(
                $"Record {record.Id} is in the FreeAgent stage but its configuration no longer has FreeAgent matching configured.");
        }

        byte[] pdfContent;
        try
        {
            pdfContent = await oneDriveIntegration.DownloadAsync(oneDriveDetails, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            const string reason = "Could not re-download the invoice from OneDrive.";
            // A transient re-download failure doesn't change what's already known: preserve any
            // attachment proof this record already carried rather than clobbering it with None.
            await MarkFreeAgentErrorAsync(
                record, actualDetails, oneDriveDetails, $"{reason} {ex.Message}", existingAttemptedAttachment, cancellationToken);
            recordActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            recordActivity?.AddException(ex);
            logger.LogError(ex, "Re-downloading the invoice from OneDrive failed for record {RecordId}; marked FreeAgentError.", record.Id);
            return new ProcessingFailed(record.Id, ex);
        }

        var fileName = invoiceFilename.Generate(
            actualDetails.ActualInvoiceDate,
            snapshot.InvoiceDescription,
            actualDetails.SourceInvoiceId.Value,
            actualDetails.ActualAmount,
            snapshot.VatMode);

        return await ProcessFreeAgentStageAsync(
            record, matching, actualDetails, oneDriveDetails, pdfContent, fileName, recordActivity, cancellationToken);
    }

    /// <summary>
    /// Matches, reconciles, and attaches the invoice to a FreeAgent bill using the supplied
    /// PDF bytes - either just retrieved/reconciled within this run, or re-downloaded by
    /// <see cref="ResumeFreeAgentStageAsync"/> for a record left mid-stage (ambiguous match,
    /// conflict, or intervention) on an earlier run. See docs/workflow-states.md's FreeAgent
    /// stage for the full state shape.
    /// </summary>
    private async Task<DueInvoiceProcessingResult> ProcessFreeAgentStageAsync(
        InvoiceRecord savedRecord,
        FreeAgentBillMatching matching,
        ActualInvoiceDetails actualDetails,
        OneDriveDetails oneDriveDetails,
        byte[] pdfContent,
        string fileName,
        Activity? recordActivity,
        CancellationToken cancellationToken)
    {
        var dateToleranceDays = matching.DateReconciliation is FreeAgentDateReconciliation dateReconciliation
            ? dateReconciliation.ToleranceDays
            : 0;
        var amountTolerance = matching.AmountReconciliation is FreeAgentAmountReconciliation amountReconciliation
            ? amountReconciliation.AmountTolerance
            : 0m;

        var criteria = new FreeAgentBillSearchCriteria(
            matching.Contact.Url,
            actualDetails.ActualInvoiceDate,
            dateToleranceDays,
            actualDetails.ActualAmount,
            amountTolerance);

        var matchResult = await freeAgentBillMatcher.FindBillAsync(criteria, cancellationToken);
        recordActivity?.AddEvent(new ActivityEvent("freeagent_match_attempted"));

        FreeAgentBillFound billFound;
        switch (matchResult)
        {
            case NoFreeAgentBillMatch noMatch:
                // Clears a prior FreeAgentError back to FreeAgentMatchExpected so the next
                // retry re-attempts matching instead of resuming an error message that no
                // longer applies. Always written (not just on a state change) since a repeat
                // no-match attempt still carries fresh diagnostic detail worth persisting.
                await EnsureFreeAgentMatchExpectedAsync(
                    savedRecord, actualDetails, oneDriveDetails, noMatch.Diagnostic, cancellationToken);
                logger.LogInformation(
                    "No FreeAgent bill matched record {RecordId}: {Diagnostic}", savedRecord.Id, noMatch.Diagnostic);
                return new ProcessingFreeAgentConflict(savedRecord.Id, "No FreeAgent bill matched the invoice.");
            case AmbiguousFreeAgentBillMatch ambiguous:
                var ambiguousDiagnostic =
                    $"{ambiguous.Candidates.Count} FreeAgent bills matched (ambiguous): " +
                    $"{string.Join(", ", ambiguous.Candidates.Select(c => c.Url))}.";
                await EnsureFreeAgentMatchExpectedAsync(
                    savedRecord, actualDetails, oneDriveDetails, ambiguousDiagnostic, cancellationToken);
                logger.LogWarning(
                    "{CandidateCount} FreeAgent bills matched record {RecordId}; never choosing among candidates.",
                    ambiguous.Candidates.Count, savedRecord.Id);
                return new ProcessingFreeAgentAmbiguous(savedRecord.Id, ambiguous.Candidates.Count);
            case FreeAgentBillFound found:
                billFound = found;
                break;
            default:
                return new ProcessingFreeAgentConflict(savedRecord.Id, "Unrecognised FreeAgent bill match result.");
        }

        var billIdentity = billFound.Bill.Identity;

        var matchedRecord = savedRecord with
        {
            State = new FreeAgentBillMatched(actualDetails, oneDriveDetails, billIdentity),
        };

        // Tracks the most recent genuine proof of our own upload to this bill, so the catch
        // block below can preserve it if a later step (including persisting the terminal
        // FreeAgentAttached state itself) then fails - a technical failure at that point
        // doesn't undo the successful FreeAgent-side upload it followed. Seeded from a prior
        // attempt's proof when this retry rematches the same bill, so a fresh reconciliation
        // failure striking before the upload step is reached doesn't discard it for nothing.
        Option<FreeAgentAttachmentMetadata> lastKnownAttachment =
            savedRecord.State is FreeAgentError { BillContext: FreeAgentErrorBillContext seed } && seed.Bill == billIdentity
                ? seed.AttemptedAttachment
                : Option.None;

        try
        {
            await recordRepository.ReplaceAsync(matchedRecord, cancellationToken);
            recordActivity?.AddEvent(new ActivityEvent("state_freeagent_bill_matched"));

            var currentBill = billFound.Bill;

            if (matching.DateReconciliation is FreeAgentDateReconciliation && currentBill.DatedOn != actualDetails.ActualInvoiceDate)
            {
                var dateResult = await freeAgentBillReconciler.ReconcileDateAsync(
                    billIdentity, actualDetails.ActualInvoiceDate, cancellationToken);
                var outcome = await HandleReconciliationResultAsync(
                    matchedRecord, actualDetails, oneDriveDetails, billIdentity, dateResult, lastKnownAttachment, cancellationToken);
                if (outcome is DueInvoiceProcessingResult outcomeResult)
                {
                    return outcomeResult;
                }
            }

            // Amount reconciliation only ever targets a bill with exactly one item - "never
            // guess which item to change" extends to never auto-picking the only item on a
            // multi-item bill. A mismatched total on a bill with zero or multiple items is never
            // silently accepted, though - falling through to attach as though reconciled would
            // leave a wrong amount on the bill with no record of the discrepancy.
            if (matching.AmountReconciliation is FreeAgentAmountReconciliation && currentBill.TotalValue.Amount != actualDetails.ActualAmount.Amount)
            {
                if (currentBill.Items.Count != 1)
                {
                    const string reason =
                        "FreeAgent bill amount does not match the invoice and the bill does not have exactly one item to reconcile.";
                    await MarkFreeAgentErrorAsync(matchedRecord, actualDetails, oneDriveDetails, reason, Option.None, cancellationToken);
                    return new ProcessingFreeAgentConflict(matchedRecord.Id, reason);
                }

                var item = currentBill.Items[0].ItemUrl;
                var amountResult = await freeAgentBillReconciler.ReconcileItemAmountAsync(
                    billIdentity, item, actualDetails.ActualAmount, cancellationToken);
                var outcome = await HandleReconciliationResultAsync(
                    matchedRecord, actualDetails, oneDriveDetails, billIdentity, amountResult, lastKnownAttachment, cancellationToken);
                if (outcome is DueInvoiceProcessingResult outcomeResult)
                {
                    return outcomeResult;
                }

                // The reconciler verified the item itself reflects the requested amount, but the
                // actual goal - the reason this branch ran at all - is the bill's own aggregate
                // total matching the invoice. Verify that explicitly rather than assuming the two
                // always move together (VAT/rounding could leave them apart).
                if (amountResult is FreeAgentReconciled reconciled &&
                    reconciled.Bill.TotalValue.Amount != actualDetails.ActualAmount.Amount)
                {
                    const string reason =
                        "FreeAgent accepted the item amount change but the bill's aggregate total still does not match the invoice.";
                    await MarkFreeAgentErrorAsync(matchedRecord, actualDetails, oneDriveDetails, reason, Option.None, cancellationToken);
                    return new ProcessingFreeAgentConflict(matchedRecord.Id, reason);
                }
            }

            var reconciledRecord = matchedRecord with
            {
                State = new FreeAgentBillReconciled(actualDetails, oneDriveDetails, billIdentity),
            };
            await recordRepository.ReplaceAsync(reconciledRecord, cancellationToken);
            recordActivity?.AddEvent(new ActivityEvent("state_freeagent_bill_reconciled"));

            // expectedExisting is only the exact metadata this record itself recorded having
            // POSTed to this exact bill (see FreeAgentErrorBillContext, which survives a retry
            // via the record's persisted state) - never fabricated from what we're about to
            // upload, and never carried over from a different bill a previous retry matched.
            // Any attachment already on the bill that isn't recorded as genuinely ours can only
            // be someone else's, or an earlier attempt whose outcome we don't actually know (a
            // lock, a business rejection, a technical exception) - always None in those cases.
            // FreeAgentAttachmentUploader still separately accepts a plain name/size match
            // against the file about to be uploaded even when this is None (see issue #133) -
            // e.g. after this InvoiceRecord's own history was lost - so passing Option.None here
            // means "no record-backed proof", not "treat any existing attachment as foreign".
            Option<FreeAgentAttachmentMetadata> expectedExisting =
                savedRecord.State is FreeAgentError { BillContext: FreeAgentErrorBillContext context } && context.Bill == billIdentity
                    ? context.AttemptedAttachment
                    : Option.None;

            var uploadResult = await freeAgentAttachmentUploader.UploadAsync(
                billIdentity, pdfContent, fileName, expectedExisting, cancellationToken);

            switch (uploadResult)
            {
                case FreeAgentAttachmentUploaded uploaded:
                    lastKnownAttachment = uploaded.New;
                    return await CompleteFreeAgentAttachAsync(
                        reconciledRecord, actualDetails, oneDriveDetails, billIdentity, uploaded.New, recordActivity, cancellationToken);
                case FreeAgentAttachmentReplaced replaced:
                    lastKnownAttachment = replaced.New;
                    return await CompleteFreeAgentAttachAsync(
                        reconciledRecord, actualDetails, oneDriveDetails, billIdentity, replaced.New, recordActivity, cancellationToken);
                case FreeAgentAttachmentAlreadyCorrect already:
                    lastKnownAttachment = already.Existing;
                    return await CompleteFreeAgentAttachAsync(
                        reconciledRecord, actualDetails, oneDriveDetails, billIdentity, already.Existing, recordActivity, cancellationToken);
                case FreeAgentAttachmentUnexpectedExisting:
                    {
                        const string reason = "The FreeAgent bill already has an attachment that does not match this invoice's last known upload.";
                        await MarkFreeAgentErrorAsync(reconciledRecord, actualDetails, oneDriveDetails, reason, Option.None, cancellationToken);
                        return new ProcessingFreeAgentConflict(reconciledRecord.Id, reason);
                    }
                case FreeAgentBillLocked locked:
                    {
                        var reason = $"FreeAgent bill locked: {locked.Reason}.";
                        await MarkFreeAgentErrorAsync(reconciledRecord, actualDetails, oneDriveDetails, reason, Option.None, cancellationToken);
                        return new ProcessingFreeAgentConflict(reconciledRecord.Id, reason);
                    }
                case FreeAgentVerificationFailed verificationFailed:
                    {
                        // The POST itself succeeded (this is only returned after a successful
                        // upload whose read-back verification failed), so this exact metadata is
                        // genuine proof of our own attachment on this bill - safe to hand back as
                        // expectedExisting on the next retry, as long as it still matches.
                        var attemptedAttachment = new FreeAgentAttachmentMetadata(
                            fileName, pdfContent.Length, FreeAgentAttachmentContentType.Pdf, timeProvider.GetUtcNow());
                        await MarkFreeAgentErrorAsync(
                            reconciledRecord, actualDetails, oneDriveDetails, verificationFailed.Detail, attemptedAttachment, cancellationToken);
                        return new ProcessingFreeAgentConflict(reconciledRecord.Id, verificationFailed.Detail);
                    }
                default:
                    {
                        const string reason = "Unrecognised attachment result.";
                        await MarkFreeAgentErrorAsync(reconciledRecord, actualDetails, oneDriveDetails, reason, Option.None, cancellationToken);
                        return new ProcessingFreeAgentConflict(reconciledRecord.Id, reason);
                    }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A technical failure reconciling or attaching (for example a transient FreeAgent
            // outage) after the bill was already matched and persisted: fall back to the
            // retryable FreeAgentError rather than stranding the record in
            // FreeAgentBillMatched/FreeAgentBillReconciled, both excluded from the due query.
            // lastKnownAttachment preserves proof of a successful upload if the failure struck
            // afterwards (for example persisting the terminal FreeAgentAttached state); it stays
            // None if the attach step never ran or never resolved to a known-good result.
            await MarkFreeAgentErrorAsync(matchedRecord, actualDetails, oneDriveDetails, ex.Message, lastKnownAttachment, cancellationToken);
            recordActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            recordActivity?.AddException(ex);
            logger.LogError(ex, "FreeAgent reconciliation/attachment failed for record {RecordId}; marked FreeAgentError.", matchedRecord.Id);
            return new ProcessingFailed(matchedRecord.Id, ex);
        }
    }

    /// <summary>Handles a reconciliation step's result. Returns null when reconciliation succeeded (continue the stage); otherwise returns the terminal-for-this-run result.</summary>
    /// <param name="lastKnownAttachment">
    /// Proof of an earlier successful upload to this same bill, if this is a retry that rematched
    /// it (see <see cref="ProcessFreeAgentStageAsync"/>) - none of these failure causes touch the
    /// attachment step at all, so any such proof must survive into the resulting FreeAgentError
    /// rather than being silently dropped.
    /// </param>
    private async Task<Option<DueInvoiceProcessingResult>> HandleReconciliationResultAsync(
        InvoiceRecord matchedRecord,
        ActualInvoiceDetails actualDetails,
        OneDriveDetails oneDriveDetails,
        FreeAgentBillIdentity billIdentity,
        FreeAgentReconciliationResult result,
        Option<FreeAgentAttachmentMetadata> lastKnownAttachment,
        CancellationToken cancellationToken)
    {
        DueInvoiceProcessingResult conflictResult;
        switch (result)
        {
            case FreeAgentReconciled:
                return Option.None;
            case FreeAgentItemNotOnBill:
                await MarkFreeAgentErrorAsync(
                    matchedRecord, actualDetails, oneDriveDetails, "Selected FreeAgent item does not belong to the matched bill.",
                    lastKnownAttachment, cancellationToken);
                conflictResult = new ProcessingFreeAgentConflict(matchedRecord.Id, "Selected FreeAgent item does not belong to the matched bill.");
                break;
            case FreeAgentBillLocked locked:
                await MarkFreeAgentErrorAsync(
                    matchedRecord, actualDetails, oneDriveDetails, $"FreeAgent bill locked: {locked.Reason}.", lastKnownAttachment, cancellationToken);
                conflictResult = new ProcessingFreeAgentConflict(matchedRecord.Id, $"FreeAgent bill locked: {locked.Reason}.");
                break;
            case FreeAgentPaymentInterventionRequired interventionRequired:
                {
                    var interventionOutcome = await CreateFreeAgentInterventionAsync(
                        matchedRecord, actualDetails, oneDriveDetails, interventionRequired.Intervention, cancellationToken);
                    return interventionOutcome;
                }
            case FreeAgentVerificationFailed verificationFailed:
                // This is reconciliation verification (date/amount), not attachment - no upload
                // was attempted this run, but lastKnownAttachment may still hold proof from an
                // earlier run against this same bill.
                await MarkFreeAgentErrorAsync(
                    matchedRecord, actualDetails, oneDriveDetails, verificationFailed.Detail, lastKnownAttachment, cancellationToken);
                conflictResult = new ProcessingFreeAgentConflict(matchedRecord.Id, verificationFailed.Detail);
                break;
            case FreeAgentRemoteRejected remoteRejected:
                await MarkFreeAgentErrorAsync(
                    matchedRecord, actualDetails, oneDriveDetails, remoteRejected.Detail, lastKnownAttachment, cancellationToken);
                conflictResult = new ProcessingFreeAgentConflict(matchedRecord.Id, remoteRejected.Detail);
                break;
            default:
                await MarkFreeAgentErrorAsync(
                    matchedRecord, actualDetails, oneDriveDetails, "Unrecognised reconciliation result.", lastKnownAttachment, cancellationToken);
                conflictResult = new ProcessingFreeAgentConflict(matchedRecord.Id, "Unrecognised reconciliation result.");
                break;
        }

        return conflictResult;
    }

    private async Task<DueInvoiceProcessingResult> CreateFreeAgentInterventionAsync(
        InvoiceRecord record,
        ActualInvoiceDetails actualDetails,
        OneDriveDetails oneDriveDetails,
        FreeAgentPaymentInterventionDetails details,
        CancellationToken cancellationToken)
    {
        // Guard against creating a duplicate when a concurrent run (the HTTP-triggered and
        // timer-triggered processors can overlap on the same due record) already created one -
        // not fully atomic against the same race, but catches the common case.
        if (await freeAgentInterventionRepository.HasPendingInterventionAsync(record.Id, cancellationToken))
        {
            var pendingInterventions = await freeAgentInterventionRepository.ListPendingAsync(cancellationToken);
            if (pendingInterventions.FirstOrDefault(i => i.RecordId == record.Id) is { } existing)
            {
                var alreadyPending = record with
                {
                    State = new FreeAgentInterventionPending(actualDetails, oneDriveDetails, details.Bill, existing.Id),
                };
                await recordRepository.ReplaceAsync(alreadyPending, cancellationToken);
                return new ProcessingFreeAgentInterventionRequired(record.Id, existing.Id);
            }
        }

        var interventionId = new FreeAgentInterventionId($"freeagent-intervention-{Guid.NewGuid():N}");
        var intervention = FreeAgentGuessIntervention.Create(interventionId, record.Id, details, timeProvider.GetUtcNow());
        await freeAgentInterventionRepository.CreateAsync(intervention, cancellationToken);

        var pending = record with
        {
            State = new FreeAgentInterventionPending(actualDetails, oneDriveDetails, details.Bill, interventionId),
        };
        await recordRepository.ReplaceAsync(pending, cancellationToken);
        logger.LogWarning(
            "FreeAgent amount reconciliation for record {RecordId} needs an administrator decision; intervention {InterventionId} created.",
            record.Id, interventionId);

        return new ProcessingFreeAgentInterventionRequired(record.Id, interventionId);
    }

    private async Task<DueInvoiceProcessingResult> CompleteFreeAgentAttachAsync(
        InvoiceRecord reconciledRecord,
        ActualInvoiceDetails actualDetails,
        OneDriveDetails oneDriveDetails,
        FreeAgentBillIdentity billIdentity,
        FreeAgentAttachmentMetadata attachment,
        Activity? recordActivity,
        CancellationToken cancellationToken)
    {
        var attached = reconciledRecord with
        {
            State = new FreeAgentAttached(actualDetails, oneDriveDetails, billIdentity, attachment),
        };
        await recordRepository.ReplaceAsync(attached, cancellationToken);
        recordActivity?.AddEvent(new ActivityEvent("state_freeagent_attached"));
        logger.LogInformation("Attached invoice to FreeAgent bill for record {RecordId}.", reconciledRecord.Id);
        return new ProcessingSucceeded(reconciledRecord.Id);
    }

    /// <summary>
    /// Ensures the record is at <see cref="FreeAgentMatchExpected"/>, carrying
    /// <paramref name="lastMatchDiagnostic"/> from the attempt that just failed to
    /// match - always written, even when already in this state, since a repeat
    /// no-match attempt still produces fresh diagnostic detail worth persisting
    /// (clears a prior <see cref="FreeAgentError"/> the same way).
    /// </summary>
    private async Task EnsureFreeAgentMatchExpectedAsync(
        InvoiceRecord record,
        ActualInvoiceDetails actualDetails,
        OneDriveDetails oneDriveDetails,
        Option<string> lastMatchDiagnostic,
        CancellationToken cancellationToken)
    {
        var matchExpected = record with
        {
            State = new FreeAgentMatchExpected(actualDetails, oneDriveDetails, lastMatchDiagnostic),
        };
        await recordRepository.ReplaceAsync(matchExpected, cancellationToken);
    }

    private async Task MarkFreeAgentErrorAsync(
        InvoiceRecord record,
        ActualInvoiceDetails actualDetails,
        OneDriveDetails oneDriveDetails,
        string errorMessage,
        Option<FreeAgentAttachmentMetadata> attemptedAttachment,
        CancellationToken cancellationToken)
    {
        // record's incoming state already carries the bill this error occurred against for
        // every caller past the initial match (FreeAgentBillMatched/FreeAgentBillReconciled),
        // and carries it forward from a prior FreeAgentError when resuming one (e.g. the
        // re-download failure in ResumeFreeAgentStageAsync) - None only before matching ever
        // found a bill.
        Option<FreeAgentBillIdentity> bill = record.State switch
        {
            FreeAgentBillMatched matched => matched.Bill,
            FreeAgentBillReconciled reconciled => reconciled.Bill,
            FreeAgentError { BillContext: FreeAgentErrorBillContext existing } => existing.Bill,
            _ => Option.None,
        };
        Option<FreeAgentErrorBillContext> billContext = bill is FreeAgentBillIdentity knownBill
            ? new FreeAgentErrorBillContext(knownBill, attemptedAttachment)
            : Option.None;
        var errored = record with
        {
            State = new FreeAgentError(actualDetails, oneDriveDetails, errorMessage, billContext),
        };
        await recordRepository.ReplaceAsync(errored, cancellationToken);
        logger.LogError("FreeAgent processing for record {RecordId} failed: {ErrorMessage}", record.Id, errorMessage);
    }

    /// <summary>
    /// Records a match against a file already in OneDrive. reconcile_fork: when FreeAgent
    /// matching is configured, goes straight to <see cref="FreeAgentMatchExpected"/> -
    /// <see cref="ReconciledFromOneDrive"/> is never written - and enters the FreeAgent stage
    /// using the matched file's bytes; otherwise sets <see cref="ReconciledFromOneDrive"/> (with
    /// the match reason and time), without calling the source or re-uploading.
    /// </summary>
    private async Task<DueInvoiceProcessingResult> ReconcileAsync(
        InvoiceRecord record,
        InvoiceProcessingSnapshot snapshot,
        OneDriveMatch match,
        Activity? recordActivity,
        CancellationToken cancellationToken)
    {
        if (snapshot.FreeAgentMatching is FreeAgentBillMatching)
        {
            var matchExpected = record with
            {
                State = new FreeAgentMatchExpected(match.Details, match.OneDriveDetails, Option.None),
            };
            await recordRepository.ReplaceAsync(matchExpected, cancellationToken);
            recordActivity?.AddEvent(new ActivityEvent("state_freeagent_match_expected"));
            logger.LogInformation(
                "Reconciled record {RecordId} against existing OneDrive file at {Location}; entering the FreeAgent stage.",
                record.Id, match.OneDriveDetails.OneDriveLocation);

            return await ResumeFreeAgentStageAsync(matchExpected, snapshot, recordActivity, cancellationToken);
        }

        var reconciled = record with
        {
            State = new ReconciledFromOneDrive(
                match.Details,
                match.OneDriveDetails,
                match.MatchReason,
                timeProvider.GetUtcNow()),
        };
        await recordRepository.ReplaceAsync(reconciled, cancellationToken);
        recordActivity?.AddEvent(new ActivityEvent("state_reconciled_from_onedrive"));
        logger.LogInformation(
            "Reconciled record {RecordId} against existing OneDrive file at {Location}; skipping source retrieval.",
            record.Id, match.OneDriveDetails.OneDriveLocation);

        return new ProcessingReconciled(record.Id);
    }

    /// <summary>
    /// Marks a record <see cref="RetrievalError"/> (always retryable) after a
    /// technical failure — a reconciliation search or source call that could not
    /// determine whether the invoice exists — and reports it without stopping the
    /// other records.
    /// </summary>
    private async Task<DueInvoiceProcessingResult> MarkRetrievalErrorAsync(
        InvoiceRecord record,
        Exception ex,
        Activity? recordActivity,
        string stage,
        CancellationToken cancellationToken)
    {
        var errored = record with { State = new RetrievalError(ex.Message) };
        await recordRepository.ReplaceAsync(errored, cancellationToken);
        recordActivity?.AddEvent(new ActivityEvent("state_retrieval_error"));
        recordActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        recordActivity?.AddException(ex);
        logger.LogError(ex, "{Stage} failed for invoice record {RecordId}; marked RetrievalError.", stage, record.Id);
        return new ProcessingFailed(record.Id, ex);
    }

    /// <summary>
    /// Records the absence of a source match. Within the tolerance window the
    /// record stays <see cref="Expected"/> so a later run retries it (a prior
    /// <see cref="RetrievalError"/> is cleared back to <see cref="Expected"/> once a
    /// clean poll returns no match); on or after the deadline it is set to the
    /// terminal <see cref="NotFound"/>. Because the deadline is checked against
    /// every run, a record processed for the first time after its window has
    /// elapsed goes straight to <see cref="NotFound"/>. <paramref name="diagnostic"/>
    /// (the source integration's explanation of why nothing matched) is always
    /// persisted, even when otherwise a no-op, since a repeat no-match attempt
    /// still carries fresh diagnostic detail worth keeping.
    /// </summary>
    private async Task<DueInvoiceProcessingResult> RecordNoMatchAsync(
        InvoiceRecord record,
        DateOnly asOf,
        string diagnostic,
        Activity? recordActivity,
        CancellationToken cancellationToken)
    {
        var deadline = record.ExpectedDate.AddDays(record.ProcessingSnapshot.DateToleranceDays);

        if (asOf < deadline)
        {
            await recordRepository.ReplaceAsync(record with { State = new Expected(diagnostic) }, cancellationToken);
            recordActivity?.AddEvent(new ActivityEvent("no_match_within_tolerance"));
            logger.LogInformation(
                "No invoice match found yet for record {RecordId}; still expected, within tolerance until {Deadline}: {Diagnostic}",
                record.Id,
                deadline,
                diagnostic);
            return new ProcessingNoMatch(record.Id);
        }

        var notFound = record with { State = new NotFound(diagnostic) };
        await recordRepository.ReplaceAsync(notFound, cancellationToken);
        recordActivity?.AddEvent(new ActivityEvent("state_not_found_past_deadline"));
        logger.LogWarning(
            "No invoice match found for record {RecordId} by tolerance deadline {Deadline}; marked NotFound: {Diagnostic}",
            record.Id,
            deadline,
            diagnostic);
        return new ProcessingNotFound(record.Id);
    }

    private static void SetRunSummaryTags(Activity? activity, IReadOnlyList<DueInvoiceProcessingResult> results)
    {
        if (activity is null)
            return;

        activity.SetTag("invoice.processed_count", results.Count);
        activity.SetTag("invoice.saved_count", results.Count(r => r is ProcessingSucceeded));
        activity.SetTag("invoice.reconciled_count", results.Count(r => r is ProcessingReconciled));
        activity.SetTag("invoice.no_match_count", results.Count(r => r is ProcessingNoMatch));
        activity.SetTag("invoice.not_found_count", results.Count(r => r is ProcessingNotFound));
        activity.SetTag("invoice.failed_count", results.Count(r => r is ProcessingFailed));
        activity.SetTag("invoice.freeagent_ambiguous_count", results.Count(r => r is ProcessingFreeAgentAmbiguous));
        activity.SetTag(
            "invoice.freeagent_intervention_required_count", results.Count(r => r is ProcessingFreeAgentInterventionRequired));
        activity.SetTag("invoice.freeagent_conflict_count", results.Count(r => r is ProcessingFreeAgentConflict));
    }

    private void LogRunSummary(IReadOnlyList<DueInvoiceProcessingResult> results)
    {
        logger.LogInformation(
            "Due invoice processing run complete: {ProcessedCount} processed, {SavedCount} saved, " +
            "{ReconciledCount} reconciled, {NoMatchCount} no match yet, {NotFoundCount} not found, {FailedCount} failed, " +
            "{FreeAgentAmbiguousCount} FreeAgent ambiguous, {FreeAgentInterventionCount} FreeAgent intervention required, " +
            "{FreeAgentConflictCount} FreeAgent conflict.",
            results.Count,
            results.Count(r => r is ProcessingSucceeded),
            results.Count(r => r is ProcessingReconciled),
            results.Count(r => r is ProcessingNoMatch),
            results.Count(r => r is ProcessingNotFound),
            results.Count(r => r is ProcessingFailed),
            results.Count(r => r is ProcessingFreeAgentAmbiguous),
            results.Count(r => r is ProcessingFreeAgentInterventionRequired),
            results.Count(r => r is ProcessingFreeAgentConflict));
    }
}
