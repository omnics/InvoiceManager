using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class FreeAgentAuthorizationCaptureTests
{
    private static readonly FreeAgentCompany NewCompany = new(RequireSubdomain("newaccount"));

    [Fact]
    public async Task SaveAsync_PersistsBothValues_WhenEverythingSucceeds()
    {
        var store = new FakeStore();

        await FreeAgentAuthorizationCapture.SaveAsync(store, "new-refresh-token", NewCompany);

        Assert.Equal("new-refresh-token", store.RefreshToken);
        Assert.True(await store.ReadSubdomainAsync() is FreeAgentSubdomain subdomain && subdomain.Equals(NewCompany.Subdomain));
    }

    [Fact]
    public async Task SaveAsync_LeavesThePreviousAuthorizationUntouched_WhenClearingTheOldSubdomainFails()
    {
        var store = new FakeStore
        {
            RefreshToken = "old-refresh-token",
            Subdomain = RequireSubdomain("oldaccount"),
            FailClearSubdomain = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FreeAgentAuthorizationCapture.SaveAsync(store, "new-refresh-token", NewCompany));

        // Nothing touched yet at this point - the old, still self-consistent pair survives.
        Assert.Equal("old-refresh-token", store.RefreshToken);
        Assert.True(await store.ReadSubdomainAsync() is FreeAgentSubdomain subdomain && subdomain.Value == "oldaccount");
    }

    [Fact]
    public async Task SaveAsync_NeverPairsTheNewTokenWithTheOldSubdomain_WhenSavingTheRefreshTokenFails()
    {
        var store = new FakeStore
        {
            RefreshToken = "old-refresh-token",
            Subdomain = RequireSubdomain("oldaccount"),
            FailSaveRefreshToken = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FreeAgentAuthorizationCapture.SaveAsync(store, "new-refresh-token", NewCompany));

        // The old subdomain is already gone (cleared first) - the old token was never
        // overwritten, so this degrades to "old token, no subdomain", never a wrong pairing.
        Assert.Equal("old-refresh-token", store.RefreshToken);
        Assert.True(await store.ReadSubdomainAsync() is None);
    }

    [Fact]
    public async Task SaveAsync_NeverPairsTheNewTokenWithTheOldSubdomain_WhenSavingTheNewSubdomainFails()
    {
        var store = new FakeStore
        {
            RefreshToken = "old-refresh-token",
            Subdomain = RequireSubdomain("oldaccount"),
            FailSaveSubdomain = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FreeAgentAuthorizationCapture.SaveAsync(store, "new-refresh-token", NewCompany));

        // The new refresh token is genuinely saved (a real token was issued), but the subdomain
        // stays cleared rather than reverting to - or ever holding - the old, wrong-account value.
        // This degrades to the same safe "token present, subdomain missing" state that
        // AuthorizationModel.IsFreeAgentSubdomainMissing already detects and warns about.
        Assert.Equal("new-refresh-token", store.RefreshToken);
        Assert.True(await store.ReadSubdomainAsync() is None);
    }

    private static FreeAgentSubdomain RequireSubdomain(string value) =>
        FreeAgentSubdomain.TryParse(value) is FreeAgentSubdomain subdomain
            ? subdomain
            : throw new InvalidOperationException($"'{value}' is not a valid test subdomain.");

    private sealed class FakeStore : IFreeAgentAuthorizationStore
    {
        public string? RefreshToken { get; set; }

        public FreeAgentSubdomain? Subdomain { get; set; }

        public bool FailClearSubdomain { get; set; }

        public bool FailSaveRefreshToken { get; set; }

        public bool FailSaveSubdomain { get; set; }

        public Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshToken is not null);

        public Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshToken);

        public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            if (FailSaveRefreshToken)
                throw new InvalidOperationException("Simulated Key Vault failure saving the refresh token.");

            RefreshToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task ClearRefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            RefreshToken = null;
            return Task.CompletedTask;
        }

        public Task<Option<FreeAgentSubdomain>> ReadSubdomainAsync(CancellationToken cancellationToken = default)
        {
            Option<FreeAgentSubdomain> result = Subdomain is FreeAgentSubdomain value ? value : Option.None;
            return Task.FromResult(result);
        }

        public Task SaveSubdomainAsync(FreeAgentSubdomain subdomain, CancellationToken cancellationToken = default)
        {
            if (FailSaveSubdomain)
                throw new InvalidOperationException("Simulated Key Vault failure saving the subdomain.");

            Subdomain = subdomain;
            return Task.CompletedTask;
        }

        public Task ClearSubdomainAsync(CancellationToken cancellationToken = default)
        {
            if (FailClearSubdomain)
                throw new InvalidOperationException("Simulated Key Vault failure clearing the subdomain.");

            Subdomain = null;
            return Task.CompletedTask;
        }
    }
}
