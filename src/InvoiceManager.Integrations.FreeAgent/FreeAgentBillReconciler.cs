using System.Net;
using InvoiceManager.Core.Integrations.FreeAgent;
using NodaMoney;

namespace InvoiceManager.Integrations.FreeAgent;

/// <summary>
/// Reconciles a FreeAgent bill's date and amount. Verifies item ownership before
/// any mutation request, inspects payment explanations for context (never
/// deleting anything itself), and translates a locked-field 422 into either an
/// administrator intervention (a genuine Guess is present) or a generic locked
/// conflict.
/// </summary>
internal sealed class FreeAgentBillReconciler : IFreeAgentBillReconciler
{
    private readonly FreeAgentApiClient client;

    public FreeAgentBillReconciler(FreeAgentApiClient client)
    {
        this.client = client;
    }

    private static string DescribeRejection(string summary, string? validationMessage) =>
        validationMessage is null ? $"{summary}." : $"{summary}: {validationMessage}";

    public async Task<FreeAgentReconciliationResult> ReconcileDateAsync(
        FreeAgentBillIdentity bill, DateOnly newDatedOn, CancellationToken cancellationToken = default)
    {
        var before = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);
        var beforeItemUrls = (before.BillItems ?? []).Select(i => i.Url).ToHashSet();
        var beforeDueOn = before.DueOn;
        var beforeStatus = before.Status;

        var result = await client.PutBillDateAsync(bill.Url.OriginalString, newDatedOn, cancellationToken);
        if (result.IsLocked)
        {
            // The POC only proved a payment-related lock for item-amount changes, not date
            // changes (a date lock is more plausibly a period lock, unproven either way) - so
            // this never runs the Guess-lookup detour used by the amount path below.
            return new FreeAgentBillLocked(FreeAgentLockReason.Unknown);
        }

        if (result.Value is null)
        {
            // A non-locked 422 (a normal validation rejection) - never proceed to re-read and
            // verify the bill as though the mutation might have succeeded. Anything else with no
            // bill is a success response that didn't say what happened: not a refusal, since the
            // caller attaches regardless of a refused date.
            return result.StatusCode == HttpStatusCode.UnprocessableEntity
                ? new FreeAgentRemoteRejected(DescribeRejection("FreeAgent rejected the dated_on change", result.RejectionMessage))
                : new FreeAgentVerificationFailed("FreeAgent reported success for the dated_on change but returned no bill.");
        }

        var after = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);

        // Verify the proven invariant explicitly rather than trusting it: changing dated_on
        // must preserve item URLs and due_on/status.
        var afterItemUrls = (after.BillItems ?? []).Select(i => i.Url).ToHashSet();
        if (!afterItemUrls.SetEquals(beforeItemUrls) || after.DueOn != beforeDueOn || after.Status != beforeStatus)
        {
            return new FreeAgentVerificationFailed(
                "Changing dated_on unexpectedly altered bill item URLs, due_on, or status.");
        }

        var newDatedOnText = newDatedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(after.DatedOn, newDatedOnText, StringComparison.Ordinal))
        {
            return new FreeAgentVerificationFailed(
                "FreeAgent accepted the dated_on change but the bill's date does not reflect it.");
        }

        return new FreeAgentReconciled(after.ToSnapshot());
    }

    public async Task<FreeAgentReconciliationResult> ReconcileItemAmountAsync(
        FreeAgentBillIdentity bill,
        FreeAgentBillItemIdentity item,
        Money newTotalValue,
        CancellationToken cancellationToken = default)
    {
        var current = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);
        var ownsItem = (current.BillItems ?? []).Any(i => i.Url == item.Url);
        if (!ownsItem)
            return new FreeAgentItemNotOnBill();

        var result = await client.PutBillItemAmountAsync(
            bill.Url.OriginalString, item.Url.OriginalString, newTotalValue.Amount, cancellationToken);
        if (result.IsLocked)
            return await ClassifyLockedAsync(bill, item, result.LockedFieldDetail!, newTotalValue, cancellationToken);

        if (result.Value is null)
            return new FreeAgentRemoteRejected(DescribeRejection("FreeAgent did not return an updated bill", result.RejectionMessage));

        // Verify against an independent read, not the PUT response body - an optimistic or
        // stale mutation response could echo back what was requested without FreeAgent having
        // actually persisted it, same reasoning as the date and attachment paths' re-GET.
        var updated = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);
        var updatedItem = (updated.BillItems ?? []).FirstOrDefault(i => i.Url == item.Url);
        if (updatedItem?.TotalValue is not { } updatedTotalText ||
            !decimal.TryParse(updatedTotalText, System.Globalization.CultureInfo.InvariantCulture, out var updatedTotal) ||
            updatedTotal != newTotalValue.Amount)
        {
            return new FreeAgentVerificationFailed(
                "FreeAgent accepted the item amount change but the item's total_value does not reflect it.");
        }

        // Accept FreeAgent's VAT/net/rounding values as-is - never recompute locally.
        return new FreeAgentReconciled(updated.ToSnapshot());
    }

    /// <summary>
    /// A locked-field 422 was returned. Inspects payment explanations for context: a genuine
    /// unapproved Guess produces an intervention (never deleted here); any other locked
    /// response is a generic conflict.
    /// </summary>
    private async Task<FreeAgentReconciliationResult> ClassifyLockedAsync(
        FreeAgentBillIdentity bill,
        FreeAgentBillItemIdentity item,
        string lockedDetail,
        Money proposedAmount,
        CancellationToken cancellationToken)
    {
        var bankAccountUrls = await client.GetBankAccountUrlsAsync(cancellationToken);
        var lookup = await FreeAgentPaymentGuard.FindGuessForBillAsync(
            client, bankAccountUrls, bill.Url.OriginalString, cancellationToken);

        if (lookup.Outcome != GuessLookup.ExactlyOne || lookup.Explanation is not { } explanation)
            return new FreeAgentBillLocked(FreeAgentLockReason.CachedTotalLocked);

        var currentBill = await client.GetBillAsync(bill.Url.OriginalString, cancellationToken);
        var currency = currentBill.Currency ?? proposedAmount.Currency.Code;
        var currentAmount = decimal.TryParse(currentBill.TotalValue, System.Globalization.CultureInfo.InvariantCulture, out var total)
            ? new Money(total, currency)
            : new Money(0m, currency);

        var details = new FreeAgentPaymentInterventionDetails(
            bill,
            item,
            explanation.BankTransaction?.OriginalString ?? string.Empty,
            explanation.Url?.OriginalString ?? string.Empty,
            currentAmount,
            proposedAmount,
            $"FreeAgent rejected the amount change ({lockedDetail}); an unapproved Guess payment explanation is attached.");

        return new FreeAgentPaymentInterventionRequired(details);
    }
}
