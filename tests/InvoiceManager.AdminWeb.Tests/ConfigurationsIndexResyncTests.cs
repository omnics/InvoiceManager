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
    public async Task ResyncStuckRecord_RequiresConfirmation_ForAFreeAgentInterventionPendingRecord()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(
            Records.Build(
                config,
                state: new FreeAgentInterventionPending(
                    Actuals.Build(),
                    new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item"),
                    new FreeAgentInterventionId("intervention-1"))));
        var trigger = new FakeInvoiceRecordResyncTrigger();
        var model = CreateModel(config, records, trigger);

        var result = await model.OnPostResyncStuckRecordAsync("acme", config.IntegrationType, confirmed: false);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.False(trigger.WasTriggered);
        Assert.Equal(
            "This resync would supersede a pending Guess-removal intervention without a decision being " +
            "recorded. Confirm before continuing.",
            model.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task ResyncStuckRecord_Proceeds_ForAFreeAgentInterventionPendingRecord_OnceConfirmed()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(
            Records.Build(
                config,
                state: new FreeAgentInterventionPending(
                    Actuals.Build(),
                    new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item"),
                    new FreeAgentInterventionId("intervention-1"))));
        var trigger = new FakeInvoiceRecordResyncTrigger();
        var model = CreateModel(config, records, trigger);

        await model.OnPostResyncStuckRecordAsync("acme", config.IntegrationType, confirmed: true);

        Assert.True(trigger.WasTriggered);
    }

    [Fact]
    public async Task ResyncStuckRecord_ProceedsWithoutConfirmation_ForAStateThatDoesNotNeedIt()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(
            Records.Build(config, state: new RetrievalError("transient failure")));
        var trigger = new FakeInvoiceRecordResyncTrigger();
        var model = CreateModel(config, records, trigger);

        await model.OnPostResyncStuckRecordAsync("acme", config.IntegrationType, confirmed: false);

        Assert.True(trigger.WasTriggered);
    }

    private static IndexModel CreateModel(
        InvoiceConfiguration config, InMemoryInvoiceRecordRepository records, FakeInvoiceRecordResyncTrigger trigger)
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
            records,
            trigger)
        {
            PageContext = new PageContext { HttpContext = httpContext },
        };
        model.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());
        return model;
    }

    private sealed class FakeInvoiceRecordResyncTrigger : IInvoiceRecordResyncTrigger
    {
        public bool WasTriggered { get; private set; }

        public Task<InvoiceRecordResyncTriggerResult> TriggerAsync(
            InvoiceConfigurationId configurationId,
            IntegrationType integrationType,
            InvoiceConfigurationActor actor,
            CancellationToken cancellationToken)
        {
            WasTriggered = true;
            return Task.FromResult<InvoiceRecordResyncTriggerResult>(
                new InvoiceRecordResyncTriggerSucceeded(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), configurationId)));
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
