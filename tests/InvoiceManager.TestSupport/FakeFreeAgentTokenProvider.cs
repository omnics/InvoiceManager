using InvoiceManager.Infrastructure.FreeAgentAuthorization;

namespace InvoiceManager.TestSupport;

/// <summary>
/// A token provider test double that returns a fixed token, or throws a configured
/// exception to simulate an expired/revoked FreeAgent authorization.
/// </summary>
public sealed class FakeFreeAgentTokenProvider(string token = "fake-access-token", Exception? failure = null)
    : IFreeAgentTokenProvider
{
    public Task<string> AcquireTokenAsync(CancellationToken cancellationToken = default)
    {
        return failure is null ? Task.FromResult(token) : Task.FromException<string>(failure);
    }
}
