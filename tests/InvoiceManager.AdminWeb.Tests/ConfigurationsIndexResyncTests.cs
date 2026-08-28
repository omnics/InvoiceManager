using InvoiceManager.AdminWeb.Pages.Configurations;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using InvoiceManager.TestSupport;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace InvoiceManager.AdminWeb.Tests;

public sealed class ConfigurationsIndexResyncTests
{
    [Fact]
    public async Task ResyncStuckRecord_PassesConfirmedThroughToTheTrigger_AndSurfacesSucceeded()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var trigger = new FakeInvoiceRecordResyncTrigger(
            new InvoiceRecordResyncTriggerSucceeded(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), config.Id)));
        var model = CreateModel(config, trigger);

        var result = await model.OnPostResyncStuckRecordAsync("acme", config.IntegrationType, confirmed: true);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.True(trigger.LastConfirmed);
        Assert.Equal(
            "The most recent record was refreshed from the current configuration and reset to Expected; it will " +
            "be retried the next time this configuration is processed (skipped while it is inactive).",
            model.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task ResyncStuckRecord_SurfacesConfirmationRequiredMessage_WhenTheTriggerReportsIt()
    {
        // This page always shows the "Confirm" checkbox next to the button (it doesn't otherwise
        // know a row's record state), but the real gate against superseding a pending
        // intervention is enforced by the Functions/Core resync operation, not this handler -
        // it just surfaces whatever that operation reports.
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var trigger = new FakeInvoiceRecordResyncTrigger(
            new InvoiceRecordResyncTriggerConfirmationRequired(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), config.Id)));
        var model = CreateModel(config, trigger);

        var result = await model.OnPostResyncStuckRecordAsync("acme", config.IntegrationType, confirmed: false);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.False(trigger.LastConfirmed);
        Assert.Equal(
            "This resync would supersede a pending Guess-removal intervention without a decision being " +
            "recorded. Confirm before continuing.",
            model.TempData["StatusMessage"]);
    }

    private static IndexModel CreateModel(InvoiceConfiguration config, FakeInvoiceRecordResyncTrigger trigger)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", "actor-oid"), new Claim(ClaimTypes.Name, "Admin User")], "Test"))
        };
        var model = new IndexModel(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)),
            new FakeMicrosoftAuthorizationStore(hasTokenCache: true),
            new FakeMicrosoftResourceDiscovery(),
            trigger)
        {
            PageContext = new PageContext { HttpContext = httpContext },
        };
        model.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());
        return model;
    }

    private sealed class FakeInvoiceRecordResyncTrigger(InvoiceRecordResyncTriggerResult result) : IInvoiceRecordResyncTrigger
    {
        public bool LastConfirmed { get; private set; }

        public Task<InvoiceRecordResyncTriggerResult> TriggerAsync(
            InvoiceConfigurationId configurationId,
            IntegrationType integrationType,
            InvoiceConfigurationActor actor,
            bool confirmed,
            CancellationToken cancellationToken)
        {
            LastConfirmed = confirmed;
            return Task.FromResult(result);
        }
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

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
