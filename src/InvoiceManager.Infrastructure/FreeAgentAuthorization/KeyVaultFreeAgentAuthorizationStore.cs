using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
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

    public async Task<Option<FreeAgentSubdomain>> ReadSubdomainAsync(CancellationToken cancellationToken = default)
    {
        var raw = await secretStoreClient.GetSecretAsync(subdomainSecretName, cancellationToken);
        return FreeAgentSubdomain.TryParse(raw);
    }

    public Task SaveSubdomainAsync(FreeAgentSubdomain subdomain, CancellationToken cancellationToken = default)
    {
        return secretStoreClient.SetSecretAsync(subdomainSecretName, subdomain.Value, cancellationToken);
    }

    public Task ClearSubdomainAsync(CancellationToken cancellationToken = default)
    {
        // Overwrites with an empty value rather than deleting: FreeAgentAuthorizationCapture
        // clears this secret immediately before setting it to a new value, and a real Key
        // Vault delete leaves the secret soft-deleted - AzureKeyVaultSecretStoreClient.SetSecretAsync
        // then has to recover it (restoring its *old* value) before it can retry the set, so if
        // that retry itself failed, the secret would be left holding the recovered old value -
        // exactly the stale, wrong-account subdomain this whole capture exists to prevent. An
        // empty value parses to Option.None via FreeAgentSubdomain.TryParse just like a missing
        // secret does, without ever soft-deleting it.
        return secretStoreClient.SetSecretAsync(subdomainSecretName, string.Empty, cancellationToken);
    }
}
