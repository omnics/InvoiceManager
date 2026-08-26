using Azure.Core;
using Azure.Identity;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Core.Repositories;
using InvoiceManager.Infrastructure;
using InvoiceManager.Infrastructure.CosmosDb;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using InvoiceManager.Integrations.FreeAgent;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Behind the Azure Container Apps ingress, TLS terminates at the proxy and the app receives
// plain HTTP with the original scheme in X-Forwarded-Proto. Honor it so HttpsRedirection and
// the OIDC callback URL are built as https:// (an http:// callback fails Entra validation).
// The ingress IP is dynamic and is the only route to the container, so the default
// known-proxy allowlist is cleared.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var configuredKeyVaultUri = builder.Configuration.GetValue<Uri?>("KeyVault:Uri");
if (configuredKeyVaultUri is not null && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Configuration.AddAzureKeyVault(configuredKeyVaultUri, new DefaultAzureCredential());
}

builder.Services
    .AddOptions<KeyVaultOptions>()
    .Bind(builder.Configuration.GetSection(KeyVaultOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<KeyVaultOptions>, KeyVaultOptionsValidator>();
builder.Services
    .AddOptions<MicrosoftAuthorizationOptions>()
    .Bind(builder.Configuration.GetSection(MicrosoftAuthorizationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MicrosoftAuthorizationOptions>, MicrosoftAuthorizationOptionsValidator>();
builder.Services.AddOptions<AdminAuthorizationOptions>()
    .Bind(builder.Configuration.GetSection(AdminAuthorizationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<FreeAgentOptions>()
    .Bind(builder.Configuration.GetSection(FreeAgentOptions.SectionName));
builder.Services
    .AddOptions<FreeAgentAuthorizationOptions>()
    .Bind(builder.Configuration.GetSection(FreeAgentAuthorizationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<FreeAgentAuthorizationOptions>, FreeAgentAuthorizationOptionsValidator>();

builder.Services.AddSingleton<IMicrosoftAuthorizationStore>(serviceProvider =>
{
    var keyVaultUri = serviceProvider.GetRequiredService<IOptions<KeyVaultOptions>>().Value.Uri;
    var secretStoreClient = new AzureKeyVaultSecretStoreClient(keyVaultUri);
    return new KeyVaultMicrosoftAuthorizationStore(
        secretStoreClient,
        serviceProvider.GetRequiredService<IOptions<MicrosoftAuthorizationOptions>>());
});
builder.Services.AddFreeAgentIntegration();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect()
    .AddOpenIdConnect(MicrosoftOpenIdConnectOptionsSetup.WorkflowAuthorizationScheme, _ => { })
    // A dedicated, otherwise-unused sign-in scheme for the FreeAgent ticket. Unlike the Microsoft
    // OIDC flow above (which re-authenticates the same admin from a real ID token's claims, so
    // signing back into the shared cookie is a harmless refresh), OnCreatingTicket below adds no
    // claims - it only persists the refresh token to Key Vault. Without this, the OAuth handler's
    // default SignInScheme falls back to the app's shared admin cookie and would overwrite the
    // operator's authenticated session with a claimless principal, failing the AdminGroup policy
    // on the very next request.
    .AddCookie(FreeAgentOAuthOptionsSetup.TransientSignInScheme, options =>
    {
        options.Cookie.Name = ".InvoiceManager.FreeAgentTransient";
    })
    .AddOAuth(FreeAgentOAuthOptionsSetup.WorkflowAuthorizationScheme, _ => { });
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, MicrosoftOpenIdConnectOptionsSetup>();
builder.Services.AddSingleton<IConfigureOptions<OAuthOptions>, FreeAgentOAuthOptionsSetup>();

var adminGroupPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .AddRequirements(new AdminGroupRequirement())
    .Build();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AdminGroupAuthorizationHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminGroup", adminGroupPolicy)
    .SetFallbackPolicy(adminGroupPolicy);

// Shared credential for outbound service-to-service calls (the container binds it to the
// AdminWeb user-assigned managed identity via AZURE_CLIENT_ID). Used to mint a token for
// the Easy Auth-protected Functions app.
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());

builder.Services.AddHttpClient<FunctionsHealthCheck>();
builder.Services.AddHttpClient<IExpectedRecordGenerationTrigger, FunctionsExpectedRecordGenerationTrigger>();
builder.Services.AddSingleton(_ => CosmosClientFactory.Create(builder.Configuration));
builder.Services.AddSingleton<IInvoiceConfigurationRepository>(sp =>
    new CosmosInvoiceConfigurationRepository(
        sp.GetRequiredService<CosmosClient>(),
        builder.Configuration["CosmosDatabase"] ?? "invoicemanager"));
builder.Services.AddSingleton<IInvoiceRecordRepository>(sp =>
    new CosmosInvoiceRecordRepository(
        sp.GetRequiredService<CosmosClient>(),
        builder.Configuration["CosmosDatabase"] ?? "invoicemanager",
        sp.GetRequiredService<ILogger<CosmosInvoiceRecordRepository>>()));
builder.Services.AddSingleton<InvoiceConfigurationService>();
builder.Services.AddSingleton<IMicrosoftTokenProvider, MicrosoftTokenProvider>();
builder.Services.AddOptions<MicrosoftResourceDiscoveryOptions>()
    .Bind(builder.Configuration.GetSection(MicrosoftResourceDiscoveryOptions.SectionName));
builder.Services.AddHttpClient<IMicrosoftResourceDiscovery, MicrosoftResourceDiscovery>()
    .AddStandardResilienceHandler();
builder.Services
    .AddHealthChecks()
    .AddCheck<CosmosHealthCheck>("cosmos")
    .AddCheck<FunctionsHealthCheck>("functions")
    .AddCheck<FreeAgentAuthorizationHealthCheck>("freeagent-authorization")
    .AddCheck<MicrosoftAuthorizationHealthCheck>("microsoft-authorization");
builder.Services.AddRazorPages();

var app = builder.Build();

// Must run before anything that inspects the request scheme (HttpsRedirection, auth).
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

public partial class Program
{
}

internal sealed class AdminGroupRequirement : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

internal sealed class AdminGroupAuthorizationHandler(IOptions<AdminAuthorizationOptions> options)
    : Microsoft.AspNetCore.Authorization.AuthorizationHandler<AdminGroupRequirement>
{
    protected override Task HandleRequirementAsync(
        Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context,
        AdminGroupRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            context.User.FindAll("groups").Any(claim => claim.Value == options.Value.GroupObjectId))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

internal sealed class MicrosoftOpenIdConnectOptionsSetup
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string WorkflowAuthorizationScheme = "WorkflowAuthorization";
    // The auth-code redemption may only request scopes for a SINGLE resource; the
    // Entra v2 token endpoint rejects a mixed-resource scope set with AADSTS28000.
    // We redeem for Microsoft Graph alone, whose only job here is to seed the MSAL
    // cache with a refresh token + account. Consent for the other resources (ARM
    // billing, Graph Files.ReadWrite.All) is gathered on the interactive authorize
    // leg below, so downstream AcquireTokenSilent calls can mint per-resource tokens
    // for either resource from that same refresh token. openid/profile/offline_access
    // are reserved scopes MSAL adds automatically.
    private static readonly string[] DownstreamScopes =
    [
        "User.Read"
    ];

    private readonly IOptions<MicrosoftAuthorizationOptions> microsoftAuthorizationOptions;

    public MicrosoftOpenIdConnectOptionsSetup(
        IOptions<MicrosoftAuthorizationOptions> microsoftAuthorizationOptions)
    {
        this.microsoftAuthorizationOptions = microsoftAuthorizationOptions;
    }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name is not (OpenIdConnectDefaults.AuthenticationScheme or WorkflowAuthorizationScheme))
        {
            return;
        }

        var authOptions = microsoftAuthorizationOptions.Value;
        options.Authority = authOptions.Authority;
        options.ClientId = authOptions.ClientId;
        options.ClientSecret = authOptions.ClientSecret;
        var isWorkflowAuthorization = name == WorkflowAuthorizationScheme;
        options.CallbackPath = isWorkflowAuthorization ? "/workflow-signin-oidc" : "/signin-oidc";
        options.ResponseType = OpenIdConnectResponseType.Code;
        // Entra's form_post response is rejected by .NET 11's automatic cross-origin CSRF
        // protection before the OIDC handler can validate its state and correlation values.
        // A code-only flow supports query mode and remains protected by PKCE and OIDC state.
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.SaveTokens = false;
        options.UsePkce = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        if (isWorkflowAuthorization)
        {
            options.Scope.Add("offline_access");
            options.Scope.Add("User.Read");
            options.Scope.Add("https://management.azure.com/user_impersonation");
            options.Scope.Add("https://graph.microsoft.com/Files.ReadWrite.All");
            options.Scope.Add("https://graph.microsoft.com/Mail.Read");
        }
        options.TokenValidationParameters.NameClaimType = "name";
        if (isWorkflowAuthorization)
            options.Events.OnAuthorizationCodeReceived = async context =>
        {
            var currentAuthOptions = context.HttpContext.RequestServices
                .GetRequiredService<IOptions<MicrosoftAuthorizationOptions>>()
                .Value;

            var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}{context.Options.CallbackPath}";
            var application = ConfidentialClientApplicationBuilder
                .Create(currentAuthOptions.ClientId)
                .WithClientSecret(currentAuthOptions.ClientSecret)
                .WithAuthority(currentAuthOptions.Authority)
                .WithRedirectUri(redirectUri)
                .Build();

            var authorizationStore = context.HttpContext.RequestServices
                .GetRequiredService<IMicrosoftAuthorizationStore>();
            MsalTokenCacheBinding.Bind(application.UserTokenCache, authorizationStore);

            var codeVerifier = context.TokenEndpointRequest?.GetParameter(OAuthConstants.CodeVerifierKey);
            if (string.IsNullOrWhiteSpace(codeVerifier) &&
                context.Properties is not null &&
                context.Properties.Items.TryGetValue(OAuthConstants.CodeVerifierKey, out var storedCodeVerifier))
            {
                codeVerifier = storedCodeVerifier;
            }

            if (string.IsNullOrWhiteSpace(codeVerifier))
            {
                throw new InvalidOperationException(
                    "The OpenID Connect authorization response did not include the PKCE code verifier.");
            }

            var result = await application
                .AcquireTokenByAuthorizationCode(DownstreamScopes, context.ProtocolMessage.Code)
                .WithPkceCodeVerifier(codeVerifier)
                .ExecuteAsync();

            context.HandleCodeRedemption(result.AccessToken, result.IdToken);
        };
    }

    public void Configure(OpenIdConnectOptions options)
    {
        Configure(Options.DefaultName, options);
    }
}

internal sealed class FreeAgentOAuthOptionsSetup : IConfigureNamedOptions<OAuthOptions>
{
    public const string WorkflowAuthorizationScheme = "FreeAgentWorkflowAuthorization";
    public const string TransientSignInScheme = "FreeAgentTransientSignIn";

    private readonly IOptions<FreeAgentOptions> freeAgentOptions;
    private readonly IOptions<FreeAgentAuthorizationOptions> authorizationOptions;

    public FreeAgentOAuthOptionsSetup(
        IOptions<FreeAgentOptions> freeAgentOptions,
        IOptions<FreeAgentAuthorizationOptions> authorizationOptions)
    {
        this.freeAgentOptions = freeAgentOptions;
        this.authorizationOptions = authorizationOptions;
    }

    public void Configure(string? name, OAuthOptions options)
    {
        if (name != WorkflowAuthorizationScheme)
        {
            return;
        }

        var environment = freeAgentOptions.Value.Environment;
        var currentAuthorizationOptions = authorizationOptions.Value;

        options.ClientId = currentAuthorizationOptions.ClientId ?? string.Empty;
        options.ClientSecret = currentAuthorizationOptions.ClientSecret ?? string.Empty;
        options.AuthorizationEndpoint = FreeAgentHosts.AuthorizationEndpoint(environment).ToString();
        options.TokenEndpoint = FreeAgentHosts.TokenEndpoint(environment).ToString();
        options.CallbackPath = "/freeagent-authorization/callback";
        // See the AddCookie(TransientSignInScheme, ...) comment in Program.cs: without this, the
        // handler's default SignInScheme falls back to the shared admin cookie and would
        // overwrite the operator's session with this ticket's claimless principal.
        options.SignInScheme = TransientSignInScheme;
        // FreeAgent's OAuth app is a confidential client (it has a client secret); PKCE is for
        // public clients and isn't part of FreeAgent's documented authorization-code flow.
        options.UsePkce = false;
        // Never persist the raw OAuth tokens in the auth cookie/properties - the refresh token is
        // written straight to Key Vault below instead.
        options.SaveTokens = false;

        options.Events.OnCreatingTicket = async context =>
        {
            if (string.IsNullOrWhiteSpace(context.RefreshToken))
            {
                throw new InvalidOperationException(
                    "FreeAgent's token response did not include a refresh token.");
            }

            var authorizationStore = context.HttpContext.RequestServices
                .GetRequiredService<IFreeAgentAuthorizationStore>();
            await authorizationStore.SaveRefreshTokenAsync(context.RefreshToken, context.HttpContext.RequestAborted);
        };
    }

    public void Configure(OAuthOptions options)
    {
        Configure(Options.DefaultName, options);
    }
}

internal sealed class CosmosHealthCheck(IServiceProvider serviceProvider, ILogger<CosmosHealthCheck> logger)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolved here rather than via constructor injection: CosmosClientFactory.Create
            // throws synchronously when Cosmos configuration is missing, and constructor
            // injection would let that throw during health-check activation itself, turning a
            // missing/unreachable Cosmos into an unhandled 500 instead of a clean Unhealthy result.
            var cosmosClient = serviceProvider.GetRequiredService<CosmosClient>();
            await cosmosClient.ReadAccountAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin web Cosmos health check failed.");
            return HealthCheckResult.Unhealthy("Cosmos DB is not reachable.", ex);
        }
    }
}

internal sealed class FunctionsHealthCheck(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<FunctionsHealthCheck> logger)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var functionsBaseUrl = configuration.GetValue<Uri?>("Functions:BaseUrl");
        if (functionsBaseUrl is null)
        {
            return HealthCheckResult.Unhealthy("Functions:BaseUrl is not configured.");
        }

        try
        {
            var healthUri = new Uri(functionsBaseUrl, "/api/health");
            using var response = await httpClient.GetAsync(healthUri, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy();
            }

            return HealthCheckResult.Unhealthy(
                $"Functions app health endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin web Functions health check failed.");
            return HealthCheckResult.Unhealthy("Functions app is not reachable.", ex);
        }
    }
}

internal sealed class FreeAgentAuthorizationHealthCheck(
    IFreeAgentAuthorizationStore authorizationStore,
    IFreeAgentTokenProvider tokenProvider,
    IOptions<FreeAgentAuthorizationOptions> authorizationOptions,
    ILogger<FreeAgentAuthorizationHealthCheck> logger)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!authorizationOptions.Value.HasClientConfiguration)
        {
            return HealthCheckResult.Unhealthy(
                "FreeAgentAuthorization:ClientId and ClientSecret are not configured.");
        }

        if (!await authorizationStore.HasRefreshTokenAsync(cancellationToken))
        {
            return HealthCheckResult.Unhealthy(
                "No FreeAgent authorization has been captured. An administrator must authorize " +
                "FreeAgent on the Authorization page.");
        }

        try
        {
            // Acquiring a token exercises the same refresh path production traffic uses. The
            // access token this returns is cached in-process for ~1 hour by FreeAgentTokenProvider,
            // so repeated health checks do not force the rotating refresh token to be consumed on
            // every page view - only when that cache has actually expired.
            await tokenProvider.AcquireTokenAsync(cancellationToken);
            return HealthCheckResult.Healthy("FreeAgent authorization is valid.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin web FreeAgent authorization health check failed.");
            return HealthCheckResult.Unhealthy("FreeAgent authorization is invalid or expired.", ex);
        }
    }
}

internal sealed class MicrosoftAuthorizationHealthCheck(
    IMicrosoftAuthorizationStore authorizationStore,
    IMicrosoftTokenProvider tokenProvider,
    IOptions<MicrosoftAuthorizationOptions> authorizationOptions,
    ILogger<MicrosoftAuthorizationHealthCheck> logger)
    : IHealthCheck
{
    // User.Read is requested during every sign-in (see MicrosoftOpenIdConnectOptionsSetup), so it
    // is always already consented - this check only needs to confirm the cached refresh token can
    // still silently mint an access token, not exercise any particular downstream API.
    private static readonly string[] Scopes = ["User.Read"];

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!authorizationOptions.Value.HasEntraConfiguration || !authorizationOptions.Value.HasClientSecret)
        {
            return HealthCheckResult.Unhealthy(
                "MicrosoftAuthorization:TenantId, ClientId, and ClientSecret are not fully configured.");
        }

        if (!await authorizationStore.HasTokenCacheAsync(cancellationToken))
        {
            return HealthCheckResult.Unhealthy(
                "No Microsoft authorization has been captured. An administrator must authorize " +
                "Microsoft on the Authorization page.");
        }

        try
        {
            await tokenProvider.AcquireTokenAsync(Scopes, cancellationToken);
            return HealthCheckResult.Healthy("Microsoft authorization is valid.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin web Microsoft authorization health check failed.");
            return HealthCheckResult.Unhealthy("Microsoft authorization is invalid or expired.", ex);
        }
    }
}
