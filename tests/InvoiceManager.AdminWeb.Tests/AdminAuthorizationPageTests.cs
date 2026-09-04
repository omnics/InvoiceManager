using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using InvoiceManager.AdminWeb.Pages;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core.Repositories;
using InvoiceManager.TestSupport;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Text.Encodings.Web;

namespace InvoiceManager.AdminWeb.Tests;

public sealed class AdminAuthorizationPageTests
{
    [Fact]
    public async Task SignIn_RequestsMailReadScope_SoConsentCoversTheEmailInvoiceSource()
    {
        await using var factory = CreateConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get("WorkflowAuthorization");

        Assert.Contains("https://graph.microsoft.com/Mail.Read", options.Scope);
    }

    [Theory]
    [InlineData(OpenIdConnectDefaults.AuthenticationScheme)]
    [InlineData("WorkflowAuthorization")]
    public async Task SignIn_UsesQueryResponseMode_ToAvoidCrossOriginFormPostCsrfRejection(
        string scheme)
    {
        await using var factory = CreateConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(scheme);

        Assert.Equal(OpenIdConnectResponseMode.Query, options.ResponseMode);
    }

    [Fact]
    public async Task OrdinarySignIn_DoesNotWriteTheSharedWorkflowTokenCache()
    {
        await using var factory = CreateConfiguredFactory();
        using var scope = factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        var ordinary = monitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Events.OnAuthorizationCodeReceived;
        var workflow = monitor.Get("WorkflowAuthorization").Events.OnAuthorizationCodeReceived;
        Assert.NotNull(ordinary);
        Assert.NotNull(workflow);
        Assert.NotEqual(ordinary.Method, workflow.Method);
    }

    [Fact]
    public async Task FreeAgentSignIn_UsesATransientSignInScheme_SoItNeverOverwritesTheAdminSession()
    {
        // The generic OAuth handler used for FreeAgent adds no claims in OnCreatingTicket (unlike
        // the Microsoft OIDC flow, which re-derives real claims from an ID token) - if this ever
        // regresses to the default SignInScheme, completing FreeAgent authorization would replace
        // the operator's authenticated admin cookie with a claimless principal and fail the
        // AdminGroup policy on the very next request.
        await using var factory = CreateConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<OAuthOptions>>()
            .Get("FreeAgentWorkflowAuthorization");

        Assert.Equal("FreeAgentTransientSignIn", options.SignInScheme);
        Assert.NotEqual(CookieAuthenticationDefaults.AuthenticationScheme, options.SignInScheme);
    }

    [Fact]
    public async Task SignedInUserOutsideAdminGroup_IsForbidden()
    {
        await using var factory = CreateConfiguredFactory(isGroupMember: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedUser_IsChallengedForTheWholeSite()
    {
        await using var factory = CreateConfiguredFactory(isAuthenticated: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Configurations");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task App_FailsFast_WhenAuthorizationConfigurationIsMissing()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.Sources.Clear();
                });
            });

        var exception = Assert.ThrowsAny<Exception>(
            () => factory.CreateClient());

        Assert.Contains("MicrosoftAuthorization:TenantId is required.", exception.ToString());
    }

    [Fact]
    public async Task AuthorizationPage_RendersStatus_WhenAuthorizationConfigurationIsPresent()
    {
        await using var factory = CreateConfiguredFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Authorization");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Microsoft authorization", body);
        Assert.Contains("FreeAgent authorization", body);
        Assert.Contains("Not captured", body);
        Assert.Contains("Capture Microsoft authorization", body);
        Assert.Contains("Capture FreeAgent authorization", body);
        Assert.DoesNotContain("Reset authorization", body);
        Assert.DoesNotContain("Set MicrosoftAuthorization", body);
        Assert.DoesNotContain("Set FreeAgentAuthorization", body);
    }

    [Fact]
    public async Task AuthorizationPage_RendersSignInAndResetActions_WhenAuthorizationIsCapturedAndUserIsNotSignedIn()
    {
        await using var factory = CreateConfiguredFactory(hasTokenCache: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Authorization");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Ready", body);
        Assert.Contains("Replace Microsoft authorization", body);
        Assert.Contains("Reset authorization", body);
        Assert.DoesNotContain("Capture Microsoft authorization", body);
    }

    [Fact]
    public async Task AuthorizationPage_RendersFreeAgentReadyAndResetAction_WhenFreeAgentAuthorizationIsCaptured()
    {
        await using var factory = CreateConfiguredFactory(hasFreeAgentRefreshToken: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Authorization");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Replace FreeAgent authorization", body);
        Assert.DoesNotContain("Capture FreeAgent authorization", body);
    }

    [Fact]
    public async Task AuthorizationPage_TreatsMicrosoftAndFreeAgentCaptureStateIndependently()
    {
        await using var factory = CreateConfiguredFactory(hasTokenCache: true, hasFreeAgentRefreshToken: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Authorization");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Replace Microsoft authorization", body);
        Assert.Contains("Capture FreeAgent authorization", body);
    }

    [Fact]
    public async Task AuthorizationPageModel_ShowsAuthorizeAction_WhenUserIsSignedInAndAuthorizationIsNotCaptured()
    {
        var model = CreateAuthorizationModel(hasTokenCache: false, isSignedIn: true);

        await model.OnGetAsync();

        Assert.True(model.ShowAuthorizeButton);
        Assert.Equal("Capture Microsoft authorization", model.AuthorizeButtonCaption);
        Assert.True(model.IsSignedIn);
        Assert.False(model.IsAuthorizationCaptured);
        Assert.True(model.ShowFreeAgentAuthorizeButton);
        Assert.Equal("Capture FreeAgent authorization", model.FreeAgentAuthorizeButtonCaption);
        Assert.False(model.IsFreeAgentAuthorizationCaptured);
    }

    [Fact]
    public async Task AuthorizationPageModel_OffersExplicitReplacement_WhenAuthorizationIsCaptured()
    {
        var model = CreateAuthorizationModel(hasTokenCache: true, isSignedIn: true, hasFreeAgentRefreshToken: true);

        await model.OnGetAsync();

        Assert.True(model.ShowAuthorizeButton);
        Assert.Equal("Replace Microsoft authorization", model.AuthorizeButtonCaption);
        Assert.True(model.IsSignedIn);
        Assert.True(model.IsAuthorizationCaptured);
        Assert.True(model.ShowFreeAgentAuthorizeButton);
        Assert.Equal("Replace FreeAgent authorization", model.FreeAgentAuthorizeButtonCaption);
        Assert.True(model.IsFreeAgentAuthorizationCaptured);
    }

    [Fact]
    public async Task AuthorizationPageModel_OnPostResetFreeAgent_ClearsRefreshTokenAndSetsStatusMessage()
    {
        var freeAgentStore = new FakeFreeAgentAuthorizationStore(hasRefreshToken: true);
        var model = CreateAuthorizationModel(hasTokenCache: false, isSignedIn: true, freeAgentAuthorizationStore: freeAgentStore);

        await model.OnPostResetFreeAgentAsync();

        Assert.False(await freeAgentStore.HasRefreshTokenAsync());
    }

    [Fact]
    public async Task AuthorizationPageModel_OnPostResetFreeAgent_AlsoClearsTheCapturedSubdomain()
    {
        // Otherwise a subsequent authorization of a different FreeAgent account would keep
        // pointing "Open FreeAgent bill" links at the previous account's subdomain.
        var freeAgentStore = new FakeFreeAgentAuthorizationStore(hasRefreshToken: true);
        await freeAgentStore.SaveSubdomainAsync(
            FreeAgentSubdomain.TryParse("previousaccount") is FreeAgentSubdomain value
                ? value
                : throw new InvalidOperationException("Test subdomain did not parse."));
        var model = CreateAuthorizationModel(hasTokenCache: false, isSignedIn: true, freeAgentAuthorizationStore: freeAgentStore);

        await model.OnPostResetFreeAgentAsync();

        Assert.True(await freeAgentStore.ReadSubdomainAsync() is None);
    }

    private static WebApplicationFactory<Program> CreateConfiguredFactory(
        bool hasTokenCache = false,
        bool hasFreeAgentRefreshToken = false,
        bool isGroupMember = true,
        bool isAuthenticated = true,
        bool useTestAuthentication = true)
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
                        ["FreeAgentAuthorization:ClientSecret"] = "freeagent-client-secret"
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IMicrosoftAuthorizationStore>(
                        new FakeMicrosoftAuthorizationStore(hasTokenCache));
                    services.AddSingleton<IFreeAgentAuthorizationStore>(
                        new FakeFreeAgentAuthorizationStore(hasFreeAgentRefreshToken));
                    if (useTestAuthentication)
                    {
                        services.AddSingleton(new TestIdentity(isAuthenticated, isGroupMember));
                        services.AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme = "Test";
                            options.DefaultChallengeScheme = "Test";
                            options.DefaultForbidScheme = "Test";
                        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
                    }
                });
            });
    }

    private static AuthorizationModel CreateAuthorizationModel(
        bool hasTokenCache,
        bool isSignedIn,
        bool hasFreeAgentRefreshToken = false,
        IFreeAgentAuthorizationStore? freeAgentAuthorizationStore = null)
    {
        var model = new AuthorizationModel(
            new FakeMicrosoftAuthorizationStore(hasTokenCache),
            Options.Create(new MicrosoftAuthorizationOptions
            {
                TenantId = "11111111-1111-1111-1111-111111111111",
                ClientId = "22222222-2222-2222-2222-222222222222",
                ClientSecret = "client-secret"
            }),
            Options.Create(new KeyVaultOptions
            {
                Uri = new Uri("https://example.vault.azure.net/")
            }),
            freeAgentAuthorizationStore ?? new FakeFreeAgentAuthorizationStore(hasFreeAgentRefreshToken),
            Options.Create(new FreeAgentAuthorizationOptions
            {
                ClientId = "freeagent-client-id",
                ClientSecret = "freeagent-client-secret"
            }));

        var identity = isSignedIn
            ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "Admin User")], "Test")
            : new ClaimsIdentity();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        model.PageContext = new PageContext
        {
            HttpContext = httpContext
        };
        model.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());

        return model;
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>();
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class FakeMicrosoftAuthorizationStore : IMicrosoftAuthorizationStore
    {
        private readonly bool hasTokenCache;

        public FakeMicrosoftAuthorizationStore(bool hasTokenCache)
        {
            this.hasTokenCache = hasTokenCache;
        }

        public Task<bool> HasTokenCacheAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(hasTokenCache);
        }

        public Task<byte[]?> ReadTokenCacheAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<byte[]?>(null);
        }

        public Task SaveTokenCacheAsync(byte[] tokenCache, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearTokenCacheAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFreeAgentAuthorizationStore : IFreeAgentAuthorizationStore
    {
        private bool hasRefreshToken;

        public FakeFreeAgentAuthorizationStore(bool hasRefreshToken)
        {
            this.hasRefreshToken = hasRefreshToken;
        }

        public FreeAgentSubdomain? Subdomain { get; private set; }

        public Task<bool> HasRefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(hasRefreshToken);
        }

        public Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(hasRefreshToken ? "refresh-token" : null);
        }

        public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            hasRefreshToken = true;
            return Task.CompletedTask;
        }

        public Task ClearRefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            hasRefreshToken = false;
            return Task.CompletedTask;
        }

        public Task<Option<FreeAgentSubdomain>> ReadSubdomainAsync(CancellationToken cancellationToken = default)
        {
            Option<FreeAgentSubdomain> result = Subdomain is FreeAgentSubdomain value ? value : Option.None;
            return Task.FromResult(result);
        }

        public Task SaveSubdomainAsync(FreeAgentSubdomain subdomain, CancellationToken cancellationToken = default)
        {
            Subdomain = subdomain;
            return Task.CompletedTask;
        }

        public Task ClearSubdomainAsync(CancellationToken cancellationToken = default)
        {
            Subdomain = null;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ConfigurationList_RemainsAvailableWithoutWorkflowAuthorization_WhileMutationsAreDisabled()
    {
        await using var factory = CreateConfiguredFactory(hasTokenCache: false)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IInvoiceConfigurationRepository>(
                    new FakeConfigurationRepository(Configurations.Build(isActive: false)));
                services.AddSingleton<IInvoiceRecordRepository>(new InMemoryInvoiceRecordRepository());
            }));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Configurations");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Test Invoice", body);
        Assert.Contains("Microsoft authorization is not captured", body);
        Assert.Contains("<button type=\"button\" class=\"primary-action\" disabled", body);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestIdentity testIdentity)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!testIdentity.IsAuthenticated)
                return Task.FromResult(AuthenticateResult.NoResult());

            const string groupId = "33333333-3333-3333-3333-333333333333";
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Admin User"),
                new Claim(ClaimTypes.NameIdentifier, "44444444-4444-4444-4444-444444444444"),
                new Claim("oid", "44444444-4444-4444-4444-444444444444"),
                new Claim("admin_group", groupId),
                new Claim("groups", testIdentity.IsGroupMember ? groupId : "55555555-5555-5555-5555-555555555555"),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed record TestIdentity(bool IsAuthenticated, bool IsGroupMember);
}
