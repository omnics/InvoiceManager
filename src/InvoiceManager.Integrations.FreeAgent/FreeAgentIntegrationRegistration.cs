using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Integrations.FreeAgent;

/// <summary>
/// Registers the FreeAgent integration's public Core interfaces. Keeps the
/// wire-level client and its implementations internal to this project - callers
/// (the Functions composition root) depend only on the Core interfaces, mirroring
/// <c>GraphOneDriveRegistration</c>'s shape.
/// </summary>
public static class FreeAgentIntegrationRegistration
{
    // FreeAgent rejects any request with no User-Agent header at all (a plain .NET HttpClient
    // sends none by default, unlike a browser) with a 400 and the body
    // {"errors":{"error":{"message":"User agent http header not set"}}} - confirmed against the
    // real sandbox API. Every FreeAgent host, including the OAuth token endpoint, needs this set.
    private const string UserAgent = "InvoiceManager/1.0 (+https://github.com/omnics/InvoiceManager)";

    public static IServiceCollection AddFreeAgentIntegration(this IServiceCollection services)
    {
        services.AddHttpClient<FreeAgentApiClient>(client => client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent))
            .AddStandardResilienceHandler();

        services.AddSingleton<IFreeAgentAuthorizationStore>(sp =>
        {
            var keyVaultUri = sp.GetRequiredService<IOptions<KeyVaultOptions>>().Value.Uri;
            var secretStoreClient = new AzureKeyVaultSecretStoreClient(keyVaultUri);
            return new KeyVaultFreeAgentAuthorizationStore(
                secretStoreClient, sp.GetRequiredService<IOptions<FreeAgentAuthorizationOptions>>());
        });
        services.AddHttpClient(nameof(FreeAgentTokenProvider), client => client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent));
        services.AddSingleton<IFreeAgentTokenProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new FreeAgentTokenProvider(
                factory.CreateClient(nameof(FreeAgentTokenProvider)),
                sp.GetRequiredService<IFreeAgentAuthorizationStore>(),
                sp.GetRequiredService<IOptions<FreeAgentOptions>>(),
                sp.GetRequiredService<IOptions<FreeAgentAuthorizationOptions>>());
        });
        services.AddHttpClient<IFreeAgentCompanyLookup, FreeAgentCompanyLookup>(
            client => client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent));

        services.AddTransient<IFreeAgentBillMatcher, FreeAgentBillMatcher>();
        services.AddTransient<IFreeAgentBillReconciler, FreeAgentBillReconciler>();
        services.AddTransient<IFreeAgentAttachmentUploader, FreeAgentAttachmentUploader>();
        services.AddTransient<IFreeAgentGuessRemover, FreeAgentGuessRemover>();
        services.AddTransient<IFreeAgentContactDirectory, FreeAgentContactDirectory>();

        return services;
    }
}
