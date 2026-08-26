using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using InvoiceManager.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace InvoiceManager.AdminWeb.Tests;

public sealed class AuthorizationHealthCheckTests
{
    [Fact]
    public async Task FreeAgentCheck_ReportsUnhealthy_WhenNotYetAuthorized()
    {
        var report = await CheckHealthAsync(
            freeAgentAuthorizationStore: new FakeFreeAgentAuthorizationStore(hasRefreshToken: false));

        Assert.Equal(HealthStatus.Unhealthy, report.Entries["freeagent-authorization"].Status);
    }

    [Fact]
    public async Task FreeAgentCheck_ReportsHealthy_WhenTokenAcquisitionSucceeds()
    {
        var report = await CheckHealthAsync(
            freeAgentAuthorizationStore: new FakeFreeAgentAuthorizationStore(hasRefreshToken: true),
            freeAgentTokenProvider: new FakeFreeAgentTokenProvider());

        Assert.Equal(HealthStatus.Healthy, report.Entries["freeagent-authorization"].Status);
    }

    [Fact]
    public async Task FreeAgentCheck_ReportsUnhealthy_WhenTokenAcquisitionFails()
    {
        var report = await CheckHealthAsync(
            freeAgentAuthorizationStore: new FakeFreeAgentAuthorizationStore(hasRefreshToken: true),
            freeAgentTokenProvider: new FakeFreeAgentTokenProvider(
                failure: new InvalidOperationException("FreeAgent token refresh failed: 400 Bad Request.")));

        var entry = report.Entries["freeagent-authorization"];
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
    }

    [Fact]
    public async Task MicrosoftCheck_ReportsUnhealthy_WhenNotYetAuthorized()
    {
        var report = await CheckHealthAsync(
            microsoftAuthorizationStore: new FakeMicrosoftAuthorizationStore(hasTokenCache: false));

        Assert.Equal(HealthStatus.Unhealthy, report.Entries["microsoft-authorization"].Status);
    }

    [Fact]
    public async Task MicrosoftCheck_ReportsHealthy_WhenTokenAcquisitionSucceeds()
    {
        var report = await CheckHealthAsync(
            microsoftAuthorizationStore: new FakeMicrosoftAuthorizationStore(hasTokenCache: true),
            microsoftTokenProvider: new FakeMicrosoftTokenProvider());

        Assert.Equal(HealthStatus.Healthy, report.Entries["microsoft-authorization"].Status);
    }

    [Fact]
    public async Task MicrosoftCheck_ReportsUnhealthy_WhenTokenAcquisitionFails()
    {
        var report = await CheckHealthAsync(
            microsoftAuthorizationStore: new FakeMicrosoftAuthorizationStore(hasTokenCache: true),
            microsoftTokenProvider: new FakeMicrosoftTokenProvider(
                failure: new InvalidOperationException("No delegated account is available in the MSAL token cache.")));

        var entry = report.Entries["microsoft-authorization"];
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
    }

    [Fact]
    public async Task AuthorizationChecks_AreTaggedToBeExcludedFromTheAnonymousHealthEndpoint()
    {
        // These checks consume a rotating FreeAgent refresh token and mint live Microsoft/FreeAgent
        // tokens, so /health's Predicate must exclude them from the anonymous endpoint (only the
        // authenticated ServiceStatus page, which runs every registered check with no predicate,
        // should trigger them) - assert on the tag that predicate depends on directly.
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var registrations = scope.ServiceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        var authorizationChecks = registrations.Where(r => r.Name is "freeagent-authorization" or "microsoft-authorization");
        Assert.NotEmpty(authorizationChecks);
        Assert.All(authorizationChecks, r => Assert.Contains("authorization", r.Tags));

        var otherChecks = registrations.Where(r => r.Name is "cosmos" or "functions");
        Assert.NotEmpty(otherChecks);
        Assert.All(otherChecks, r => Assert.DoesNotContain("authorization", r.Tags));
    }

    private static async Task<HealthReport> CheckHealthAsync(
        IFreeAgentAuthorizationStore? freeAgentAuthorizationStore = null,
        IFreeAgentTokenProvider? freeAgentTokenProvider = null,
        IMicrosoftAuthorizationStore? microsoftAuthorizationStore = null,
        IMicrosoftTokenProvider? microsoftTokenProvider = null)
    {
        await using var factory = CreateFactory(
            freeAgentAuthorizationStore, freeAgentTokenProvider, microsoftAuthorizationStore, microsoftTokenProvider);

        using var scope = factory.Services.CreateScope();
        var healthCheckService = scope.ServiceProvider.GetRequiredService<HealthCheckService>();
        return await healthCheckService.CheckHealthAsync(
            registration => registration.Name is "freeagent-authorization" or "microsoft-authorization");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IFreeAgentAuthorizationStore? freeAgentAuthorizationStore = null,
        IFreeAgentTokenProvider? freeAgentTokenProvider = null,
        IMicrosoftAuthorizationStore? microsoftAuthorizationStore = null,
        IMicrosoftTokenProvider? microsoftTokenProvider = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.Sources.Clear();
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MicrosoftAuthorization:TenantId"] = "11111111-1111-1111-1111-111111111111",
                        ["MicrosoftAuthorization:ClientId"] = "22222222-2222-2222-2222-222222222222",
                        ["MicrosoftAuthorization:ClientSecret"] = "client-secret",
                        ["KeyVault:Uri"] = "https://example.vault.azure.net/",
                        ["MicrosoftAuthorization:TokenCacheSecretName"] = "MicrosoftAuthorization--MsalTokenCache",
                        ["AdminAuthorization:GroupObjectId"] = "33333333-3333-3333-3333-333333333333",
                        ["FreeAgentAuthorization:ClientId"] = "freeagent-client-id",
                        ["FreeAgentAuthorization:ClientSecret"] = "freeagent-client-secret",
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IFreeAgentAuthorizationStore>(
                        freeAgentAuthorizationStore ?? new FakeFreeAgentAuthorizationStore(hasRefreshToken: true));
                    services.AddSingleton<IFreeAgentTokenProvider>(
                        freeAgentTokenProvider ?? new FakeFreeAgentTokenProvider());
                    services.AddSingleton<IMicrosoftAuthorizationStore>(
                        microsoftAuthorizationStore ?? new FakeMicrosoftAuthorizationStore(hasTokenCache: true));
                    services.AddSingleton<IMicrosoftTokenProvider>(
                        microsoftTokenProvider ?? new FakeMicrosoftTokenProvider());
                });
            });
    }

    private sealed class FakeMicrosoftAuthorizationStore(bool hasTokenCache) : IMicrosoftAuthorizationStore
    {
        public Task<bool> HasTokenCacheAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(hasTokenCache);

        public Task<byte[]?> ReadTokenCacheAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task SaveTokenCacheAsync(byte[] tokenCache, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearTokenCacheAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeFreeAgentAuthorizationStore(bool hasRefreshToken) : IFreeAgentAuthorizationStore
    {
        public Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(hasRefreshToken);

        public Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(hasRefreshToken ? "refresh-token" : null);

        public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
