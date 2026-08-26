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
            0 => new NoFreeAgentBillMatch(),
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
}
