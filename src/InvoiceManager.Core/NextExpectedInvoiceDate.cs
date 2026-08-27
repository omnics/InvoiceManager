namespace InvoiceManager.Core;

/// <summary>The next expected invoice date for a configuration.</summary>
public sealed record NextExpectedDate(DateOnly Date);

/// <summary>
/// Indicates the most recent invoice record has not yet reached the success
/// state, so no further expected invoice should be created for now.
/// </summary>
public sealed record InvoiceInProgress;

/// <summary>The outcome of calculating the next expected invoice date.</summary>
public union NextExpectedDateResult(NextExpectedDate, InvoiceInProgress);

/// <summary>
/// Derives the next expected invoice date from a configuration and the most
/// recent invoice record, if any. Today's date is deliberately not consulted.
/// </summary>
public static class NextExpectedInvoiceDate
{
    public static NextExpectedDateResult CalculateNext(
        InvoiceConfiguration configuration,
        Option<InvoiceRecord> mostRecentRecord)
    {
        return mostRecentRecord switch
        {
            None => new NextExpectedDate(configuration.StartDate),
            InvoiceRecord record => NextFromRecord(record, configuration.Frequency),
        };
    }

    // A record has reached the success state once its file is in OneDrive, either
    // by being saved there or reconciled against a file already present. Those
    // states carry the actual invoice date the next expected date is derived from.
    // The FreeAgent states are all reached only after SavedToOneDrive/
    // ReconciledFromOneDrive (see docs/workflow-states.md), so every one of them
    // carries the same actual invoice date that state did and must compute the
    // same next date from it - generation is idempotent per period, not per state,
    // regardless of which of these states the record has since progressed to by
    // the time this next runs.
    private static NextExpectedDateResult NextFromRecord(
        InvoiceRecord record,
        InvoiceFrequency frequency) =>
        record.State switch
        {
            SavedToOneDrive saved =>
                new NextExpectedDate(AddFrequency(saved.ActualDetails.ActualInvoiceDate, frequency)),
            ReconciledFromOneDrive reconciled =>
                new NextExpectedDate(AddFrequency(reconciled.ActualDetails.ActualInvoiceDate, frequency)),
            FreeAgentMatchExpected matchExpected =>
                new NextExpectedDate(AddFrequency(matchExpected.ActualDetails.ActualInvoiceDate, frequency)),
            FreeAgentBillMatched matched =>
                new NextExpectedDate(AddFrequency(matched.ActualDetails.ActualInvoiceDate, frequency)),
            FreeAgentBillReconciled freeAgentReconciled =>
                new NextExpectedDate(AddFrequency(freeAgentReconciled.ActualDetails.ActualInvoiceDate, frequency)),
            FreeAgentAttached attached =>
                new NextExpectedDate(AddFrequency(attached.ActualDetails.ActualInvoiceDate, frequency)),
            FreeAgentInterventionPending pending =>
                new NextExpectedDate(AddFrequency(pending.ActualDetails.ActualInvoiceDate, frequency)),
            FreeAgentError freeAgentError =>
                new NextExpectedDate(AddFrequency(freeAgentError.ActualDetails.ActualInvoiceDate, frequency)),
            // Non-success states produce no next record. For terminal NotFound this
            // deliberately stops the recurrence: a missing invoice is assumed to mean
            // the subscription was cancelled, so resuming a genuinely-skipped period
            // requires manual intervention. See docs/domain-model.md
            // (Next-Expected Creation and Cancellation).
            Expected or NotFound or RetrievalError or Retrieved =>
                new InvoiceInProgress(),
        };

    private static DateOnly AddFrequency(DateOnly date, InvoiceFrequency frequency) =>
        frequency switch
        {
            InvoiceFrequency.Monthly => date.AddMonths(1),
            _ => throw new ArgumentOutOfRangeException(
                nameof(frequency), frequency, "Unsupported invoice frequency."),
        };
}
