using InvoiceManager.AdminWeb.Pages;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using InvoiceManager.TestSupport;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceManager.AdminWeb.Tests;

// The "Generate expected records" trigger moved here from the Authorization page (see
// AdminAuthorizationPageTests) since it's an invoice-workflow action, not an authorization one.
public sealed class IndexPageTests
{
    [Fact]
    public async Task GenerateExpectedRecords_TriggersFunction_AndSurfacesResultAsStatusMessage()
    {
        var trigger = new FakeExpectedRecordGenerationTrigger(
            new ExpectedRecordGenerationTriggered(207));
        var model = CreateIndexModel(generationTrigger: trigger);

        var result = await model.OnPostGenerateExpectedRecordsAsync();

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.True(trigger.WasTriggered);
        Assert.Equal(
            "Expected record generation was triggered (HTTP 207).",
            model.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task GenerateExpectedRecords_ReportsMissingConfiguration_WhenFunctionsUrlIsNotConfigured()
    {
        var trigger = new FakeExpectedRecordGenerationTrigger(
            new ExpectedRecordGenerationNotConfigured());
        var model = CreateIndexModel(generationTrigger: trigger);

        await model.OnPostGenerateExpectedRecordsAsync();

        Assert.Equal(
            "The Functions app URL is not configured, so expected record generation could not be triggered.",
            model.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task OnGetAsync_ShowsCurrentAndLastCompletedRows_SortedByDateDescending()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var completed = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 6, 1),
            state: new SavedToOneDrive(
                Actuals.Build(new DateOnly(2025, 6, 1)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item")));
        var current = Records.Build(
            config, expectedDate: new DateOnly(2025, 7, 1), state: new RetrievalError("transient failure"));
        var records = new InMemoryInvoiceRecordRepository(completed, current);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);
        var model = CreateIndexModel(overview: overview, recordRepository: records);

        await model.OnGetAsync();

        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(new DateOnly(2025, 7, 1), model.Rows[0].Date);
        Assert.Equal(new DateOnly(2025, 6, 1), model.Rows[1].Date);
    }

    [Fact]
    public async Task ResyncRecord_TriggersResync_ForAnEligibleRecordThatNeedsNoConfirmation()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(
            Records.Build(config, state: new RetrievalError("transient failure")));
        var resyncTrigger = new FakeInvoiceRecordResyncTrigger();
        var model = CreateIndexModel(recordRepository: records, resyncTrigger: resyncTrigger);

        var result = await model.OnPostResyncRecordAsync("acme", config.IntegrationType, confirmed: false);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.True(resyncTrigger.WasTriggered);
        Assert.Equal(
            "The most recent record was refreshed from the current configuration and reset to Expected; it will " +
            "be retried the next time this configuration is processed (skipped while it is inactive).",
            model.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task ResyncRecord_RequiresConfirmation_ForAFreeAgentInterventionPendingRecord()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(
            Records.Build(
                config,
                state: new FreeAgentInterventionPending(
                    Actuals.Build(),
                    new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item"),
                    new FreeAgentInterventionId("intervention-1"))));
        var resyncTrigger = new FakeInvoiceRecordResyncTrigger();
        var model = CreateIndexModel(recordRepository: records, resyncTrigger: resyncTrigger);

        var result = await model.OnPostResyncRecordAsync("acme", config.IntegrationType, confirmed: false);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.False(resyncTrigger.WasTriggered);
        Assert.Equal(
            "This resync would supersede a pending Guess-removal intervention without a decision being " +
            "recorded. Confirm before continuing.",
            model.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task ResyncRecord_TriggersResync_ForAFreeAgentInterventionPendingRecord_OnceConfirmed()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(
            Records.Build(
                config,
                state: new FreeAgentInterventionPending(
                    Actuals.Build(),
                    new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item"),
                    new FreeAgentInterventionId("intervention-1"))));
        var resyncTrigger = new FakeInvoiceRecordResyncTrigger();
        var model = CreateIndexModel(recordRepository: records, resyncTrigger: resyncTrigger);

        await model.OnPostResyncRecordAsync("acme", config.IntegrationType, confirmed: true);

        Assert.True(resyncTrigger.WasTriggered);
    }

    [Fact]
    public async Task ResyncRecord_ReportsMissingAuthorization_WhenNoWorkflowAuthorizationIsCaptured()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(
            Records.Build(config, state: new RetrievalError("transient failure")));
        var resyncTrigger = new FakeInvoiceRecordResyncTrigger();
        var model = CreateIndexModel(
            recordRepository: records, resyncTrigger: resyncTrigger, hasWorkflowAuthorization: false);

        await model.OnPostResyncRecordAsync("acme", config.IntegrationType, confirmed: false);

        Assert.False(resyncTrigger.WasTriggered);
        Assert.Equal(
            "Capture Microsoft authorization before resyncing a record.",
            model.TempData["StatusMessage"]);
    }

    private static IndexModel CreateIndexModel(
        IExpectedRecordGenerationTrigger? generationTrigger = null,
        InvoiceSyncOverview? overview = null,
        InvoiceManager.Core.Repositories.IInvoiceRecordRepository? recordRepository = null,
        IInvoiceRecordResyncTrigger? resyncTrigger = null,
        bool hasWorkflowAuthorization = true)
    {
        var records = recordRepository ?? new InMemoryInvoiceRecordRepository();
        var model = new IndexModel(
            generationTrigger ?? new FakeExpectedRecordGenerationTrigger(new ExpectedRecordGenerationTriggered(207)),
            overview ?? new InvoiceSyncOverview(new InvoiceConfigurationService(new FakeConfigurationRepository()), records),
            new FakeMicrosoftAuthorizationStore(hasWorkflowAuthorization),
            records,
            resyncTrigger ?? new FakeInvoiceRecordResyncTrigger());

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", "actor-oid"), new Claim(ClaimTypes.Name, "Admin User")], "Test"))
        };
        model.PageContext = new PageContext { HttpContext = httpContext };
        model.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());

        return model;
    }

    private sealed class FakeExpectedRecordGenerationTrigger : IExpectedRecordGenerationTrigger
    {
        private readonly ExpectedRecordGenerationTriggerResult result;

        public FakeExpectedRecordGenerationTrigger(ExpectedRecordGenerationTriggerResult? result = null)
        {
            this.result = result ?? new ExpectedRecordGenerationTriggered(207);
        }

        public bool WasTriggered { get; private set; }

        public Task<ExpectedRecordGenerationTriggerResult> TriggerAsync(CancellationToken cancellationToken)
        {
            WasTriggered = true;
            return Task.FromResult(result);
        }
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
        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>();
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
