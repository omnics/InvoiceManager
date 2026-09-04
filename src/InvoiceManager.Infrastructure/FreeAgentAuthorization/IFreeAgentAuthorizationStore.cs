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
    /// null if FreeAgent has never been authorized, or authorized before this capture existed.
    /// </summary>
    Task<string?> ReadSubdomainAsync(CancellationToken cancellationToken = default);

    Task SaveSubdomainAsync(string subdomain, CancellationToken cancellationToken = default);

    Task ClearSubdomainAsync(CancellationToken cancellationToken = default);
}
