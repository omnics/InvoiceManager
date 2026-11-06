using Azure.Identity;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Integrations.FreeAgent.IntegrationTests;

/// <summary>
/// Real FreeAgent sandbox wiring shared by the sandbox tests in this project: Key Vault refresh
/// token -&gt; FreeAgentTokenProvider -&gt; FreeAgentApiClient, built the same way the Functions app
/// builds it.
///
/// Requires:
/// - The developer signed in to Azure with access to the test environment's Key Vault
///   (`az login`), the same prerequisite as the Cosmos/Microsoft integration tests.
/// - `KeyVault:Uri` configured - reuses whatever InvoiceManager.AdminWeb's local user secrets
///   already have set for Aspire, so no separate setup is needed if Aspire already runs locally.
/// - A FreeAgent refresh token already captured against the sandbox company via AdminWeb's
///   /Authorization page (FreeAgent section) - see docs/deployment.md.
/// </summary>
internal sealed class FreeAgentSandbox : IDisposable
{
    private readonly HttpClient tokenHttpClient;
    private readonly HttpClient apiHttpClient;

    private FreeAgentSandbox(
        HttpClient tokenHttpClient, HttpClient apiHttpClient, IFreeAgentTokenProvider tokenProvider, FreeAgentApiClient apiClient)
    {
        this.tokenHttpClient = tokenHttpClient;
        this.apiHttpClient = apiHttpClient;
        TokenProvider = tokenProvider;
        ApiClient = apiClient;
    }

    public IFreeAgentTokenProvider TokenProvider { get; }

    public FreeAgentApiClient ApiClient { get; }

    public static Uri ApiBaseUri => FreeAgentHosts.ApiBaseUri(FreeAgentEnvironment.Sandbox);

    /// <summary>FreeAgent rejects any request without a User-Agent header.</summary>
    public static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("InvoiceManager-IntegrationTests/1.0");
        return httpClient;
    }

    public static async Task<FreeAgentSandbox> ConnectAsync()
    {
        var localConfiguration = new ConfigurationBuilder()
            .AddUserSecrets("InvoiceManager.AdminWeb")
            .AddEnvironmentVariables()
            .Build();

        var keyVaultUri = localConfiguration.GetValue<Uri?>("KeyVault:Uri")
            ?? throw new InvalidOperationException(
                "KeyVault:Uri is not configured. Set it via `dotnet user-secrets set KeyVault:Uri " +
                "<test-environment-key-vault-uri> --project src/InvoiceManager.AdminWeb` (the same " +
                "value Aspire already uses locally), and be signed in to Azure with access to it " +
                "(`az login`).");

        // FreeAgentAuthorization:ClientId/ClientSecret are not local secrets - AdminWeb's own
        // Program.cs loads them straight from Key Vault at startup (FreeAgentAuthorization--ClientId
        // / FreeAgentAuthorization--ClientSecret) via this same provider, so this does the same
        // rather than requiring them to be duplicated into local user secrets.
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(localConfiguration)
            .AddAzureKeyVault(keyVaultUri, new DefaultAzureCredential())
            .Build();

        var authorizationOptions = configuration.GetSection(FreeAgentAuthorizationOptions.SectionName)
            .Get<FreeAgentAuthorizationOptions>() ?? new FreeAgentAuthorizationOptions();

        if (!authorizationOptions.HasClientConfiguration)
        {
            throw new InvalidOperationException(
                "FreeAgentAuthorization:ClientId and/or FreeAgentAuthorization:ClientSecret are not " +
                "configured in the test environment Key Vault - without them these tests would fail " +
                "later with an opaque token-endpoint error instead of this actionable one.");
        }

        // These tests' whole premise is exercising the sandbox company - never the real one, even
        // if a reused local/Key Vault configuration happens to have FreeAgent:Environment set to
        // Production (which would otherwise silently point a "sandbox" test, including its Key
        // Vault refresh-token rotation and any bills it creates, at production).
        var freeAgentOptions = new FreeAgentOptions { Environment = FreeAgentEnvironment.Sandbox };

        var secretStoreClient = new AzureKeyVaultSecretStoreClient(keyVaultUri);
        var authorizationStore = new KeyVaultFreeAgentAuthorizationStore(
            secretStoreClient, Options.Create(authorizationOptions));

        if (!await authorizationStore.HasRefreshTokenAsync())
        {
            throw new InvalidOperationException(
                "No FreeAgent refresh token is stored in Key Vault. Complete FreeAgent authorization " +
                "against the sandbox company via AdminWeb's /Authorization page (FreeAgent section) first.");
        }

        var tokenHttpClient = CreateHttpClient();
        var tokenProvider = new FreeAgentTokenProvider(
            tokenHttpClient, authorizationStore, Options.Create(freeAgentOptions), Options.Create(authorizationOptions));

        var apiHttpClient = CreateHttpClient();
        var apiClient = new FreeAgentApiClient(
            apiHttpClient, tokenProvider, Options.Create(freeAgentOptions), NullLogger<FreeAgentApiClient>.Instance);

        return new FreeAgentSandbox(tokenHttpClient, apiHttpClient, tokenProvider, apiClient);
    }

    public void Dispose()
    {
        tokenHttpClient.Dispose();
        apiHttpClient.Dispose();
    }
}
