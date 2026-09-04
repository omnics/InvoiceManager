using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Integrations.FreeAgent;

/// <summary>
/// Finds the FreeAgent bill matching a retrieved/reconciled invoice. Pages all
/// bills in the date/contact window (server-side filtering), then applies
/// amount-tolerance filtering locally.
/// </summary>
internal sealed class FreeAgentBillMatcher : IFreeAgentBillMatcher
{
    private const int PageSize = 25;

    private readonly FreeAgentApiClient client;

    public FreeAgentBillMatcher(FreeAgentApiClient client)
    {
        this.client = client;
    }

    public async Task<FreeAgentBillMatchResult> FindBillAsync(
        FreeAgentBillSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        var fromDate = criteria.ExpectedDate.AddDays(-criteria.DateToleranceDays);
        var toDate = criteria.ExpectedDate.AddDays(criteria.DateToleranceDays);

        var candidates = new List<BillWire>();
        var page = 1;
        while (true)
        {
            var pageResults = await client.GetBillsPageAsync(
                fromDate, toDate, criteria.ContactUrl.Url.OriginalString, page, PageSize, cancellationToken);
            if (pageResults.Count == 0)
                break;

            candidates.AddRange(pageResults);
            if (pageResults.Count < PageSize)
                break;

            page++;
        }

        var matches = candidates
            .Where(bill => MatchesAmount(bill, criteria))
            .ToList();

        return matches.Count switch
        {
            0 => new NoFreeAgentBillMatch(BuildNoMatchDiagnostic(candidates, criteria)),
            1 => new FreeAgentBillFound(matches[0].ToSnapshot()),
            _ => new AmbiguousFreeAgentBillMatch(
                matches.Select(m => new FreeAgentBillIdentity(m.Url!)).ToList()),
        };
    }

    private static bool MatchesAmount(BillWire bill, FreeAgentBillSearchCriteria criteria)
    {
        if (bill.TotalValue is not { } totalValueText ||
            !decimal.TryParse(totalValueText, System.Globalization.CultureInfo.InvariantCulture, out var totalValue))
            return false;

        if (!string.Equals(bill.Currency, criteria.ExpectedAmount.Currency.Code, StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = criteria.ExpectedAmount.Amount;
        return Math.Abs(totalValue - expected) <= criteria.AmountTolerance;
    }

    /// <summary>
    /// Explains why no candidate bill matched: the contact, date window, expected
    /// amount/tolerance, how many bills FreeAgent returned in that window (already
    /// server-side filtered by contact+date), and - if any - the nearest one's
    /// actual amount, so a FreeAgent-side price change shows up directly rather
    /// than requiring a manual bill lookup.
    /// </summary>
    private static string BuildNoMatchDiagnostic(IReadOnlyList<BillWire> candidates, FreeAgentBillSearchCriteria criteria)
    {
        var windowStart = criteria.ExpectedDate.AddDays(-criteria.DateToleranceDays);
        var windowEnd = criteria.ExpectedDate.AddDays(criteria.DateToleranceDays);
        var expected = criteria.ExpectedAmount;
        var amountDescription = $"{expected.Amount} {expected.Currency.Code} (tolerance {criteria.AmountTolerance})";

        if (candidates.Count == 0)
        {
            return $"No FreeAgent bill for {criteria.ContactDisplayName} is dated between {windowStart:yyyy-MM-dd} " +
                $"and {windowEnd:yyyy-MM-dd}. Expected amount: {amountDescription}.";
        }

        // Prefer a same-currency candidate for "nearest" - subtracting raw decimals across
        // currencies (e.g. 100 USD vs 100 GBP) would call a numerically-close foreign amount
        // "nearest" when it was never a real candidate (MatchesAmount already rejects it on
        // currency alone). Only fall back to a different-currency candidate when no
        // same-currency total exists at all, so the diagnostic still reports something.
        var parsable = candidates
            .Where(bill => bill.TotalValue is not null &&
                decimal.TryParse(bill.TotalValue, System.Globalization.CultureInfo.InvariantCulture, out _))
            .ToList();
        var sameCurrency = parsable
            .Where(bill => string.Equals(bill.Currency, expected.Currency.Code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var nearest = (sameCurrency.Count > 0 ? sameCurrency : parsable)
            .OrderBy(bill => Math.Abs(
                decimal.Parse(bill.TotalValue!, System.Globalization.CultureInfo.InvariantCulture) - expected.Amount))
            .FirstOrDefault();

        return nearest is null
            ? $"{candidates.Count} FreeAgent bill(s) found for {criteria.ContactDisplayName} dated between " +
                $"{windowStart:yyyy-MM-dd} and {windowEnd:yyyy-MM-dd}, but none had a parsable total value. " +
                $"Expected amount: {amountDescription}."
            : $"{candidates.Count} FreeAgent bill(s) found for {criteria.ContactDisplayName} dated between " +
                $"{windowStart:yyyy-MM-dd} and {windowEnd:yyyy-MM-dd}, but none matched the expected amount " +
                $"{amountDescription}; nearest was {nearest.Url} for {nearest.TotalValue} {nearest.Currency}.";
    }
}
