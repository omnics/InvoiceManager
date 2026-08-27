using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.TestSupport;

public sealed class FakeFreeAgentBillMatcher : IFreeAgentBillMatcher
{
    public FreeAgentBillMatchResult Result { get; set; } = new NoFreeAgentBillMatch("No candidate bills.");

    public List<FreeAgentBillSearchCriteria> Requests { get; } = [];

    public Task<FreeAgentBillMatchResult> FindBillAsync(
        FreeAgentBillSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        Requests.Add(criteria);
        return Task.FromResult(Result);
    }
}
