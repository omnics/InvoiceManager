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

    [Fact]
    public async Task ClearAsync_ClearsBothValues_WhenEverythingSucceeds()
    {
        var store = new FakeStore { RefreshToken = "old-refresh-token", Subdomain = RequireSubdomain("oldaccount") };

        await FreeAgentAuthorizationCapture.ClearAsync(store);

        Assert.Null(store.RefreshToken);
        Assert.True(await store.ReadSubdomainAsync() is None);
    }

    [Fact]
    public async Task ClearAsync_LeavesTheSubdomainAlreadyGone_WhenClearingTheRefreshTokenThenFails()
    {
        // Clears in the same subdomain-first order as SaveAsync: if the second clear fails, the
        // record degrades to "token present, subdomain missing" - the same safe state
        // AuthorizationModel.IsFreeAgentSubdomainMissing detects and warns about - rather than a
        // reset that reports "Not captured" while a stale subdomain still builds bill links.
        var store = new FakeStore
        {
            RefreshToken = "old-refresh-token",
            Subdomain = RequireSubdomain("oldaccount"),
            FailClearRefreshToken = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => FreeAgentAuthorizationCapture.ClearAsync(store));

        Assert.Equal("old-refresh-token", store.RefreshToken);
        Assert.True(await store.ReadSubdomainAsync() is None);
    }

    [Fact]
    public async Task SaveAsync_SerialisesConcurrentCalls_SoTwoCallbacksNeverInterleaveTheirWrites()
    {
        // Two authorization callbacks (two browser tabs, or two administrators) completing at
        // the same moment on the same AdminWeb instance must never interleave their individual
        // store calls into a cross-account pair, even though every individual call succeeds.
        var store = new FakeStore();
        var log = new List<string>();
        store.OnStep = step => log.Add(step);

        var callAEnteredClear = new TaskCompletionSource();
        var releaseCallA = new TaskCompletionSource();
        var firstClearSeen = false;
        store.BeforeClearSubdomain = async () =>
        {
            if (!firstClearSeen)
            {
                firstClearSeen = true;
                callAEnteredClear.SetResult();
                await releaseCallA.Task;
            }
        };

        // Deliberately not awaited yet: this call must reach (and block inside) its
        // ClearSubdomainAsync call, holding the gate, before call B is started below.
        var callA = FreeAgentAuthorizationCapture.SaveAsync(
            store, "token-a", new FreeAgentCompany(RequireSubdomain("accounta")));
        await callAEnteredClear.Task;

        var callB = FreeAgentAuthorizationCapture.SaveAsync(
            store, "token-b", new FreeAgentCompany(RequireSubdomain("accountb")));
        // Give call B every opportunity to (incorrectly) start while A is still gated - only A's
        // first step (already logged before it blocked) should be present.
        await Task.Delay(50);
        Assert.Equal(["clear-subdomain"], log);

        releaseCallA.SetResult();
        await Task.WhenAll(callA, callB);

        // Deterministic: A is guaranteed to acquire the gate first (B can only start once A is
        // already blocked inside its own gated section), so A's three steps must fully complete
        // before B's begin - never interleaved.
        Assert.Equal(
            [
                "clear-subdomain",
                "save-refresh-token:token-a",
                "save-subdomain:accounta",
                "clear-subdomain",
                "save-refresh-token:token-b",
                "save-subdomain:accountb",
            ],
            log);
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

        public bool FailClearRefreshToken { get; set; }

        public bool FailSaveRefreshToken { get; set; }

        public bool FailSaveSubdomain { get; set; }

        public Action<string>? OnStep { get; set; }

        public Func<Task>? BeforeClearSubdomain { get; set; }

        public Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshToken is not null);

        public Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshToken);

        public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            OnStep?.Invoke($"save-refresh-token:{refreshToken}");
            if (FailSaveRefreshToken)
                throw new InvalidOperationException("Simulated Key Vault failure saving the refresh token.");

            RefreshToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task ClearRefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            OnStep?.Invoke("clear-refresh-token");
            if (FailClearRefreshToken)
                throw new InvalidOperationException("Simulated Key Vault failure clearing the refresh token.");

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
            OnStep?.Invoke($"save-subdomain:{subdomain.Value}");
            if (FailSaveSubdomain)
                throw new InvalidOperationException("Simulated Key Vault failure saving the subdomain.");

            Subdomain = subdomain;
            return Task.CompletedTask;
        }

        public async Task ClearSubdomainAsync(CancellationToken cancellationToken = default)
        {
            OnStep?.Invoke("clear-subdomain");
            if (BeforeClearSubdomain is { } beforeClear)
                await beforeClear();
            if (FailClearSubdomain)
                throw new InvalidOperationException("Simulated Key Vault failure clearing the subdomain.");

            Subdomain = null;
        }
    }
}
