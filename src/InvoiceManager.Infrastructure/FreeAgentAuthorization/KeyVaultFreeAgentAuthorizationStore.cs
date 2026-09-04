using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

/// <summary>
/// Stores the FreeAgent OAuth refresh token as a plain string secret. Unlike Microsoft's MSAL
/// token cache, FreeAgent's OAuth is a plain rotating refresh token with no binary cache format
/// to persist, so no base64 encoding is needed - reuses the same <see cref="ISecretStoreClient"/>
/// as <see cref="KeyVaultMicrosoftAuthorizationStore"/>.
/// </summary>
public sealed class KeyVaultFreeAgentAuthorizationStore : IFreeAgentAuthorizationStore
{
    private readonly ISecretStoreClient secretStoreClient;
    private readonly string secretName;
    private readonly string subdomainSecretName;

    public KeyVaultFreeAgentAuthorizationStore(
        ISecretStoreClient secretStoreClient,
        IOptions<FreeAgentAuthorizationOptions> options)
    {
        this.secretStoreClient = secretStoreClient;
        secretName = options.Value.RefreshTokenSecretName;
        subdomainSecretName = options.Value.SubdomainSecretName;
    }

    public async Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = await ReadRefreshTokenAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(refreshToken);
    }

    public Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        return secretStoreClient.GetSecretAsync(secretName, cancellationToken);
    }

    public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("Refresh token cannot be empty.", nameof(refreshToken));
        }

        return secretStoreClient.SetSecretAsync(secretName, refreshToken, cancellationToken);
    }

    public Task ClearRefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        return secretStoreClient.DeleteSecretAsync(secretName, cancellationToken);
    }

    public Task<string?> ReadSubdomainAsync(CancellationToken cancellationToken = default)
    {
        return secretStoreClient.GetSecretAsync(subdomainSecretName, cancellationToken);
    }

    public Task SaveSubdomainAsync(string subdomain, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            throw new ArgumentException("Subdomain cannot be empty.", nameof(subdomain));
        }

        return secretStoreClient.SetSecretAsync(subdomainSecretName, subdomain, cancellationToken);
    }

    public Task ClearSubdomainAsync(CancellationToken cancellationToken = default)
    {
        return secretStoreClient.DeleteSecretAsync(subdomainSecretName, cancellationToken);
    }
}
