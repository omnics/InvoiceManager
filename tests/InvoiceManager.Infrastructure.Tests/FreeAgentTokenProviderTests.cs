using System.Net;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.TestSupport;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class FreeAgentTokenProviderTests
{
    private sealed class FakeFreeAgentAuthorizationStore : IFreeAgentAuthorizationStore
    {
        public string? StoredRefreshToken { get; set; } = "initial-refresh-token";
        public string? SavedRefreshToken { get; private set; }

        public Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StoredRefreshToken is not null);

        public Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StoredRefreshToken);

        public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            SavedRefreshToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task ClearRefreshTokenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Option<FreeAgentSubdomain>> ReadSubdomainAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Option<FreeAgentSubdomain>>(Option.None);

        public Task SaveSubdomainAsync(FreeAgentSubdomain subdomain, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearSubdomainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task AcquireTokenAsync_ParsesSnakeCaseTokenResponse_AndPersistsRotatedRefreshToken()
    {
        var handler = new StubHttpMessageHandler((request, index) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token": "new-access-token", "refresh_token": "rotated-refresh-token", "expires_in": 3600}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var httpClient = new HttpClient(handler);
        var authorizationStore = new FakeFreeAgentAuthorizationStore();
        var provider = new FreeAgentTokenProvider(
            httpClient,
            authorizationStore,
            Options.Create(new FreeAgentOptions { Environment = FreeAgentEnvironment.Sandbox }),
            Options.Create(new FreeAgentAuthorizationOptions { ClientId = "client-id", ClientSecret = "client-secret" }));

        var token = await provider.AcquireTokenAsync();

        Assert.Equal("new-access-token", token);
        Assert.Equal("rotated-refresh-token", authorizationStore.SavedRefreshToken);
    }
}
