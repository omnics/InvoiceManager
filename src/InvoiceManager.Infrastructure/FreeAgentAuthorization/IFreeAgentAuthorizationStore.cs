using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

public interface IFreeAgentAuthorizationStore
{
    Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default);

    Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default);

    Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task ClearRefreshTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The authorized FreeAgent account's web-app subdomain, captured from FreeAgent's own
    /// company resource at authorization time (see <see cref="FreeAgentCompanyLookup"/>) -
    /// <see cref="None"/> if FreeAgent has never been authorized, authorized before this capture
    /// existed, or the stored secret doesn't parse as a valid <see cref="FreeAgentSubdomain"/>
    /// (treated the same as "not known yet" rather than thrown, since a corrupted secret must
    /// never crash the dashboard - it just disables the bill link until re-authorization).
    /// </summary>
    Task<Option<FreeAgentSubdomain>> ReadSubdomainAsync(CancellationToken cancellationToken = default);

    Task SaveSubdomainAsync(FreeAgentSubdomain subdomain, CancellationToken cancellationToken = default);

    Task ClearSubdomainAsync(CancellationToken cancellationToken = default);
}
