using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class KeyVaultFreeAgentAuthorizationStoreTests
{
    [Fact]
    public async Task SaveRefreshTokenAsync_StoresPlainValueUnderConfiguredSecretName()
    {
        var secretStore = new FakeSecretStoreClient();
        var store = CreateStore(secretStore);

        await store.SaveRefreshTokenAsync("refresh-token-value");

        Assert.Equal("refresh-token-value", secretStore.Secrets["custom-freeagent-refresh-token"]);
    }

    [Fact]
    public async Task SaveRefreshTokenAsync_Throws_WhenValueIsBlank()
    {
        var store = CreateStore(new FakeSecretStoreClient());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveRefreshTokenAsync("   "));
    }

    [Fact]
    public async Task ReadRefreshTokenAsync_ReturnsStoredValue()
    {
        var secretStore = new FakeSecretStoreClient();
        secretStore.Secrets["custom-freeagent-refresh-token"] = "stored-token";
        var store = CreateStore(secretStore);

        Assert.Equal("stored-token", await store.ReadRefreshTokenAsync());
    }

    [Fact]
    public async Task HasRefreshTokenAsync_ReturnsFalse_WhenSecretIsMissing()
    {
        var store = CreateStore(new FakeSecretStoreClient());

        Assert.False(await store.HasRefreshTokenAsync());
    }

    [Fact]
    public async Task HasRefreshTokenAsync_ReturnsTrue_WhenSecretIsPresent()
    {
        var secretStore = new FakeSecretStoreClient();
        secretStore.Secrets["custom-freeagent-refresh-token"] = "stored-token";
        var store = CreateStore(secretStore);

        Assert.True(await store.HasRefreshTokenAsync());
    }

    [Fact]
    public async Task ClearRefreshTokenAsync_DeletesSecret()
    {
        var secretStore = new FakeSecretStoreClient();
        secretStore.Secrets["custom-freeagent-refresh-token"] = "stored-token";
        var store = CreateStore(secretStore);

        await store.ClearRefreshTokenAsync();

        Assert.False(secretStore.Secrets.ContainsKey("custom-freeagent-refresh-token"));
    }

    [Fact]
    public async Task SaveSubdomainAsync_StoresPlainValueUnderConfiguredSecretName()
    {
        var secretStore = new FakeSecretStoreClient();
        var store = CreateStore(secretStore);

        await store.SaveSubdomainAsync(RequireSubdomain("acmeltd"));

        Assert.Equal("acmeltd", secretStore.Secrets["custom-freeagent-subdomain"]);
    }

    [Fact]
    public async Task ReadSubdomainAsync_ReturnsStoredValue()
    {
        var secretStore = new FakeSecretStoreClient();
        secretStore.Secrets["custom-freeagent-subdomain"] = "acmeltd";
        var store = CreateStore(secretStore);

        Assert.True(await store.ReadSubdomainAsync() is FreeAgentSubdomain subdomain && subdomain.Value == "acmeltd");
    }

    [Fact]
    public async Task ReadSubdomainAsync_ReturnsNone_WhenSecretIsMissing()
    {
        var store = CreateStore(new FakeSecretStoreClient());

        Assert.True(await store.ReadSubdomainAsync() is None);
    }

    [Fact]
    public async Task ReadSubdomainAsync_ReturnsNone_WhenTheStoredSecretIsNotAValidSubdomain()
    {
        // Defends against a corrupted/tampered secret (e.g. hand-edited in the portal) - must
        // never crash the dashboard, just disable the bill link the same as "not known yet".
        var secretStore = new FakeSecretStoreClient();
        secretStore.Secrets["custom-freeagent-subdomain"] = "not a valid subdomain!";
        var store = CreateStore(secretStore);

        Assert.True(await store.ReadSubdomainAsync() is None);
    }

    private static FreeAgentSubdomain RequireSubdomain(string value) =>
        FreeAgentSubdomain.TryParse(value) is FreeAgentSubdomain subdomain
            ? subdomain
            : throw new InvalidOperationException($"'{value}' is not a valid test subdomain.");

    [Fact]
    public async Task ClearSubdomainAsync_OverwritesWithAnEmptyValue_RatherThanDeletingTheSecret()
    {
        // Deliberately not a delete: FreeAgentAuthorizationCapture clears this secret
        // immediately before setting it to a new value, and a real Key Vault delete would leave
        // it soft-deleted, forcing the following set to recover (and thus risk restoring) the
        // old value if that set's own retry then failed. An empty value still parses to
        // Option.None via FreeAgentSubdomain.TryParse (see ReadSubdomainAsync_ReturnsNone_WhenTheStoredSecretIsNotAValidSubdomain),
        // without ever soft-deleting the secret.
        var secretStore = new FakeSecretStoreClient();
        secretStore.Secrets["custom-freeagent-subdomain"] = "acmeltd";
        var store = CreateStore(secretStore);

        await store.ClearSubdomainAsync();

        Assert.Equal("", secretStore.Secrets["custom-freeagent-subdomain"]);
        Assert.True(await store.ReadSubdomainAsync() is None);
    }

    private static KeyVaultFreeAgentAuthorizationStore CreateStore(FakeSecretStoreClient secretStore)
    {
        return new KeyVaultFreeAgentAuthorizationStore(
            secretStore,
            Options.Create(new FreeAgentAuthorizationOptions
            {
                RefreshTokenSecretName = "custom-freeagent-refresh-token",
                SubdomainSecretName = "custom-freeagent-subdomain",
            }));
    }

    private sealed class FakeSecretStoreClient : ISecretStoreClient
    {
        public Dictionary<string, string> Secrets { get; } = [];

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            Secrets.TryGetValue(name, out var value);
            return Task.FromResult(value);
        }

        public Task SetSecretAsync(
            string name,
            string value,
            CancellationToken cancellationToken = default)
        {
            Secrets[name] = value;
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            Secrets.Remove(name);
            return Task.CompletedTask;
        }
    }
}
