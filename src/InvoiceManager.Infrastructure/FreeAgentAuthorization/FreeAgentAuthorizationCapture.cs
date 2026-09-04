using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

/// <summary>
/// Persists a freshly-authorized FreeAgent account's refresh token and subdomain together,
/// safely: clears any subdomain left over from a previously-authorized account before touching
/// either value, so every failure from this point on degrades to either the untouched previous
/// authorization (if the clear itself fails) or a "refresh token present, subdomain not yet
/// known" state (if either save fails) - never a refresh token paired with a stale, wrong-account
/// subdomain. The latter state is exactly what <c>AuthorizationModel.IsFreeAgentSubdomainMissing</c>
/// already detects and surfaces as a warning, so no new failure mode needs its own handling.
/// </summary>
public static class FreeAgentAuthorizationCapture
{
    public static async Task SaveAsync(
        IFreeAgentAuthorizationStore store,
        string refreshToken,
        FreeAgentCompany company,
        CancellationToken cancellationToken = default)
    {
        await store.ClearSubdomainAsync(cancellationToken);
        await store.SaveRefreshTokenAsync(refreshToken, cancellationToken);
        await store.SaveSubdomainAsync(company.Subdomain, cancellationToken);
    }
}
