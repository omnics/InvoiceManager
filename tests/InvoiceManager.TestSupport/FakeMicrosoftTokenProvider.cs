using InvoiceManager.Infrastructure.MicrosoftAuthorization;

namespace InvoiceManager.TestSupport;

/// <summary>
/// A token provider test double that returns a fixed token and records the scopes
/// it was asked for.
/// </summary>
public sealed class FakeMicrosoftTokenProvider(string token = "fake-access-token", Exception? failure = null)
    : IMicrosoftTokenProvider
{
    private readonly List<IReadOnlyCollection<string>> requestedScopes = [];

    public IReadOnlyList<IReadOnlyCollection<string>> RequestedScopes => requestedScopes;

    public Task<string> AcquireTokenAsync(
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        requestedScopes.Add(scopes);
        return failure is null ? Task.FromResult(token) : Task.FromException<string>(failure);
    }
}
