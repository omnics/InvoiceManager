using InvoiceManager.AdminWeb.Pages;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using InvoiceManager.TestSupport;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace InvoiceManager.AdminWeb.Tests;

// The "Generate expected records" trigger moved here from the Authorization page (see
// AdminAuthorizationPageTests) since it's an invoice-workflow action, not an authorization one.
public sealed class IndexPageTests
{
    [Fact]
    public async Task GenerateExpectedRecords_TriggersFunction_AndSurfacesResultAsStatusMessage()
    {
        var trigger = new FakeExpectedRecordGenerationTrigger(
            new ExpectedRecordGenerationTriggered());
        var model = CreateIndexModel(generationTrigger: trigger);

        var result = await model.OnPostGenerateExpectedRecordsAsync();

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.True(trigger.WasTriggered);
        Assert.Equal(
            "Started invoice processing at 14:32:31 UTC.",
            model.TempData["StatusMessage"]);
        Assert.Equal(false, model.TempData["StatusIsWarning"]);
    }

    [Fact]
    public async Task GenerateExpectedRecords_SurfacesBuriedErrors_WhenTheFunctionReports207WithFailures()
    {
        // GenerateExpectedRecordsHttp always answers 207 regardless of per-item outcome, so a
        // success HTTP status alone doesn't mean the run was clean - the trigger has to inspect
        // the response body to find failures like this one.
        var trigger = new FakeExpectedRecordGenerationTrigger(
            new ExpectedRecordGenerationCompletedWithErrors(["Configuration acme: boom"]));
        var model = CreateIndexModel(generationTrigger: trigger);

        await model.OnPostGenerateExpectedRecordsAsync();

        Assert.Equal(
            "Started invoice processing at 14:32:31 UTC, but 1 item(s) failed: Configuration acme: boom",
            model.TempData["StatusMessage"]);
        Assert.Equal(true, model.TempData["StatusIsWarning"]);
    }

    [Fact]
    public async Task GenerateExpectedRecords_ReportsMissingConfiguration_WhenFunctionsUrlIsNotConfigured()
    {
        var trigger = new FakeExpectedRecordGenerationTrigger(
            new ExpectedRecordGenerationNotConfigured());
        var model = CreateIndexModel(generationTrigger: trigger);

        await model.OnPostGenerateExpectedRecordsAsync();

        Assert.Equal(
            "The Functions app URL is not configured, so invoice processing could not be started.",
            model.TempData["StatusMessage"]);
        Assert.Equal(true, model.TempData["StatusIsWarning"]);
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
        var model = CreateIndexModel(overview: overview);

        await model.OnGetAsync();

        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(new DateOnly(2025, 7, 1), model.Rows[0].Date);
        Assert.Equal(new DateOnly(2025, 6, 1), model.Rows[1].Date);
    }

    [Fact]
    public async Task OnGetAsync_DefaultsToDateDescending_WhenNoSortIsRequested()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var older = Records.Build(config, expectedDate: new DateOnly(2025, 6, 1), state: new Expected(Option.None));
        var newer = Records.Build(config, expectedDate: new DateOnly(2025, 7, 1), state: new Expected(Option.None));
        var records = new InMemoryInvoiceRecordRepository(older, newer);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);
        var model = CreateIndexModel(overview: overview);

        await model.OnGetAsync();

        Assert.Equal(InvoiceSyncSortColumn.Date, model.Sort);
        Assert.True(model.SortDescending);
        Assert.Equal([new DateOnly(2025, 7, 1), new DateOnly(2025, 6, 1)], model.Rows.Select(r => r.Date));
    }

    [Fact]
    public async Task OnGetAsync_SortsByConfigurationAscending_WhenRequested()
    {
        var configA = Configurations.Build(id: new InvoiceConfigurationId("a"), invoiceDescription: "Zeta");
        var configB = Configurations.Build(id: new InvoiceConfigurationId("b"), invoiceDescription: "Alpha");
        var recordA = Records.Build(configA, expectedDate: new DateOnly(2025, 7, 1), state: new Expected(Option.None));
        var recordB = Records.Build(configB, expectedDate: new DateOnly(2025, 6, 1), state: new Expected(Option.None));
        var records = new InMemoryInvoiceRecordRepository(recordA, recordB);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(configA, configB)), records);
        var model = CreateIndexModel(overview: overview);
        model.SortParam = InvoiceSyncSortColumn.Configuration;

        await model.OnGetAsync();

        Assert.False(model.SortDescending);
        Assert.Equal(["Alpha", "Zeta"], model.Rows.Select(r => r.DisplayName));
    }

    [Theory]
    [InlineData(
        "https://omnics-my.sharepoint.com/personal/joshua_omnics_tech/Documents/Test/Bills/" +
        "Azure%20+%20Visual%20Studio/2026-06-09%20G164045971%20%C2%A340.01%20inc.pdf",
        true)] // A real Graph webUrl - Uri.IsWellFormedUriString rejects this (unencoded '+' in the path)
               // even though it's a perfectly good, working link; must not be used here.
    [InlineData("https://example.com/file.pdf", true)]
    [InlineData("http://example.com/file.pdf", false)] // Not https.
    [InlineData("01ABCDEF", false)] // OneDriveLocation's bare-item-ID fallback when Graph reports no webUrl.
    public void IsHttpsUrl_AcceptsRealWebUrls_ButRejectsNonHttpsOrNonUrlValues(string location, bool expected)
    {
        var model = CreateIndexModel();

        Assert.Equal(expected, model.IsHttpsUrl(location));
    }

    [Fact]
    public void DeriveOneDriveFolderUrl_TrimsTheFileNameOffARealWebUrl_ToGetTheContainingFolder()
    {
        // Graph's parentReference (an itemReference) has no webUrl property at all, so the
        // folder link has to be derived from the file's own webUrl - a literal server-relative
        // path under the document library for both OneDrive and SharePoint.
        var model = CreateIndexModel();
        const string fileUrl =
            "https://omnics-my.sharepoint.com/personal/joshua_omnics_tech/Documents/Test/Bills/" +
            "Azure%20+%20Visual%20Studio/2026-06-09%20G164045971%20%C2%A340.01%20inc.pdf";

        Assert.Equal(
            "https://omnics-my.sharepoint.com/personal/joshua_omnics_tech/Documents/Test/Bills/" +
            "Azure%20+%20Visual%20Studio",
            model.DeriveOneDriveFolderUrl(fileUrl));
    }

    [Theory]
    [InlineData("http://example.com/Bills/file.pdf")] // Not https.
    [InlineData("01ABCDEF")] // OneDriveLocation's bare-item-ID fallback when Graph reports no webUrl.
    [InlineData("https://example.com")] // No path at all - nothing to trim to.
    public void DeriveOneDriveFolderUrl_ReturnsNull_WhenThereIsNoUsableFolderPathToTrimTo(string fileLocation)
    {
        var model = CreateIndexModel();

        Assert.Null(model.DeriveOneDriveFolderUrl(fileLocation));
    }

    [Fact]
    public void HasAnyAction_IsFalse_WhenUnauthorizedAndTheRowHasNoUsableOneDriveOrFreeAgentLink()
    {
        // No workflow authorization (so no "Edit configuration"/Resync), a bare-item-ID OneDrive
        // fallback (not a usable link), and no matched FreeAgent bill at all - the menu must not
        // render an ellipsis onto an empty panel.
        var model = CreateIndexModel(hasWorkflowAuthorization: false);
        var row = new InvoiceSyncRow(
            new InvoiceConfigurationId("acme"), IntegrationType.MicrosoftBilling, "Acme invoice",
            IsActive: true, ExpectedDate: new DateOnly(2025, 7, 1),
            State: new RetrievalError("transient failure"), IsMostRecent: true);

        Assert.False(model.HasAnyAction(row));
    }

    [Fact]
    public void HasAnyAction_IsFalse_WhenUnauthorizedAndTheOnlyOneDriveValueIsTheNonUrlFallback()
    {
        var model = CreateIndexModel(hasWorkflowAuthorization: false);
        var actualDetails = Actuals.Build(new DateOnly(2025, 7, 1));
        var row = new InvoiceSyncRow(
            new InvoiceConfigurationId("acme"), IntegrationType.MicrosoftBilling, "Acme invoice",
            IsActive: true, ExpectedDate: new DateOnly(2025, 7, 1),
            State: new SavedToOneDrive(actualDetails, new OneDriveDetails("01ABCDEF", "test-drive", "test-item")),
            IsMostRecent: true);

        Assert.False(model.HasAnyAction(row));
    }

    [Fact]
    public void HasAnyAction_IsTrue_WhenUnauthorizedButTheRowHasAUsableOneDriveFileLink()
    {
        var model = CreateIndexModel(hasWorkflowAuthorization: false);
        var actualDetails = Actuals.Build(new DateOnly(2025, 7, 1));
        var row = new InvoiceSyncRow(
            new InvoiceConfigurationId("acme"), IntegrationType.MicrosoftBilling, "Acme invoice",
            IsActive: true, ExpectedDate: new DateOnly(2025, 7, 1),
            State: new SavedToOneDrive(
                actualDetails,
                new OneDriveDetails("https://example.com/Bills/invoice.pdf", "test-drive", "test-item")),
            IsMostRecent: true);

        Assert.True(model.HasAnyAction(row));
    }

    [Fact]
    public void HasAnyAction_IsFalse_WhenUnauthorizedAndTheMatchedBillHasNoConfiguredWebLink()
    {
        // FreeAgent:Subdomain isn't configured for this deployment, so FreeAgentBillUrl can't
        // build a link even though the row has a matched bill.
        var model = CreateIndexModel(hasWorkflowAuthorization: false, freeAgentSubdomain: "");
        var actualDetails = Actuals.Build(new DateOnly(2025, 7, 1));
        var row = new InvoiceSyncRow(
            new InvoiceConfigurationId("acme"), IntegrationType.MicrosoftBilling, "Acme invoice",
            IsActive: true, ExpectedDate: new DateOnly(2025, 7, 1),
            State: new FreeAgentBillMatched(
                actualDetails,
                new OneDriveDetails("01ABCDEF", "test-drive", "test-item"),
                new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1")),
            IsMostRecent: true);

        Assert.False(model.HasAnyAction(row));
    }

    [Fact]
    public async Task HasAnyAction_IsTrue_WhenWorkflowAuthorizationIsPresent_RegardlessOfLinks()
    {
        // "Edit configuration" is always available to an authorized session, even for a row with
        // no usable OneDrive/FreeAgent link yet. HasWorkflowAuthorization is only populated by a
        // load, so this test has to actually run one rather than just configuring the fake store.
        var model = CreateIndexModel(hasWorkflowAuthorization: true);
        await model.OnGetAsync();
        var row = new InvoiceSyncRow(
            new InvoiceConfigurationId("acme"), IntegrationType.MicrosoftBilling, "Acme invoice",
            IsActive: true, ExpectedDate: new DateOnly(2025, 7, 1),
            State: new Expected(Option.None), IsMostRecent: true);

        Assert.True(model.HasAnyAction(row));
    }

    [Fact]
    public async Task OnGetAsync_TogglesToDescending_WhenDescendingParamIsExplicitlyTrue()
    {
        var configA = Configurations.Build(id: new InvoiceConfigurationId("a"), invoiceDescription: "Zeta");
        var configB = Configurations.Build(id: new InvoiceConfigurationId("b"), invoiceDescription: "Alpha");
        var recordA = Records.Build(configA, expectedDate: new DateOnly(2025, 7, 1), state: new Expected(Option.None));
        var recordB = Records.Build(configB, expectedDate: new DateOnly(2025, 6, 1), state: new Expected(Option.None));
        var records = new InMemoryInvoiceRecordRepository(recordA, recordB);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(configA, configB)), records);
        var model = CreateIndexModel(overview: overview);
        model.SortParam = InvoiceSyncSortColumn.Configuration;
        model.DescendingParam = true;

        await model.OnGetAsync();

        Assert.True(model.SortDescending);
        Assert.Equal(["Zeta", "Alpha"], model.Rows.Select(r => r.DisplayName));
    }

    [Fact]
    public async Task GenerateExpectedRecords_RedirectsWithTheOperatorsCurrentSort_NotTheDefault()
    {
        // The rendered forms carry the current sort/descending as hidden fields (since a
        // generated `formaction` URL from asp-page-handler doesn't inherit the page's query
        // string), which model binding surfaces here via SortParam/DescendingParam - simulating
        // that POST body content directly rather than only the query string.
        var model = CreateIndexModel();
        model.SortParam = InvoiceSyncSortColumn.Configuration;
        model.DescendingParam = true;

        var result = await model.OnPostGenerateExpectedRecordsAsync();

        var redirect = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        var routeValues = Assert.IsAssignableFrom<IDictionary<string, object?>>(redirect.RouteValues);
        Assert.Equal(InvoiceSyncSortColumn.Configuration, routeValues!["sort"]);
        Assert.Equal(true, routeValues["descending"]);
    }

    [Fact]
    public async Task ResyncRecord_PassesConfirmedThroughToTheTrigger_AndSurfacesSucceeded()
    {
        var resyncTrigger = new FakeInvoiceRecordResyncTrigger(
            new InvoiceRecordResyncTriggerSucceeded(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), new("acme"))));
        var model = CreateIndexModel(resyncTrigger: resyncTrigger);

        var result = await model.OnPostResyncRecordAsync("acme", IntegrationType.MicrosoftBilling, confirmed: true);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.True(resyncTrigger.LastConfirmed);
        Assert.Equal(
            "The most recent record was refreshed from the current configuration and reset to Expected; it will " +
            "be retried the next time this configuration is processed (skipped while it is inactive).",
            model.TempData["StatusMessage"]);
        Assert.Equal(false, model.TempData["StatusIsWarning"]);
    }

    [Fact]
    public async Task ResyncRecord_SurfacesConfirmationRequiredMessage_WhenTheTriggerReportsIt()
    {
        // The page no longer pre-reads the record's state to decide whether to require
        // confirmation - it always passes `confirmed` through and reacts to whatever the
        // Functions/Core resync operation (the authority on the record's current state) reports,
        // closing the TOCTOU window a page-level pre-check alone could not.
        var resyncTrigger = new FakeInvoiceRecordResyncTrigger(
            new InvoiceRecordResyncTriggerConfirmationRequired(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), new("acme"))));
        var model = CreateIndexModel(resyncTrigger: resyncTrigger);

        var result = await model.OnPostResyncRecordAsync("acme", IntegrationType.MicrosoftBilling, confirmed: false);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.False(resyncTrigger.LastConfirmed);
        Assert.Equal(
            "This resync would supersede a pending Guess-removal intervention without a decision being " +
            "recorded. Confirm before continuing.",
            model.TempData["StatusMessage"]);
        Assert.Equal(true, model.TempData["StatusIsWarning"]);
    }

    [Fact]
    public async Task ResyncRecord_ReportsMissingAuthorization_WhenNoWorkflowAuthorizationIsCaptured()
    {
        var resyncTrigger = new FakeInvoiceRecordResyncTrigger(
            new InvoiceRecordResyncTriggerSucceeded(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), new("acme"))));
        var model = CreateIndexModel(resyncTrigger: resyncTrigger, hasWorkflowAuthorization: false);

        await model.OnPostResyncRecordAsync("acme", IntegrationType.MicrosoftBilling, confirmed: false);

        Assert.False(resyncTrigger.WasTriggered);
        Assert.Equal(
            "Capture Microsoft authorization before resyncing a record.",
            model.TempData["StatusMessage"]);
        Assert.Equal(true, model.TempData["StatusIsWarning"]);
    }

    private static IndexModel CreateIndexModel(
        IExpectedRecordGenerationTrigger? generationTrigger = null,
        InvoiceSyncOverview? overview = null,
        IInvoiceRecordResyncTrigger? resyncTrigger = null,
        bool hasWorkflowAuthorization = true,
        TimeProvider? timeProvider = null,
        string freeAgentSubdomain = "acmeltd")
    {
        var records = new InMemoryInvoiceRecordRepository();
        var model = new IndexModel(
            generationTrigger ?? new FakeExpectedRecordGenerationTrigger(new ExpectedRecordGenerationTriggered()),
            overview ?? new InvoiceSyncOverview(new InvoiceConfigurationService(new FakeConfigurationRepository()), records),
            new FakeMicrosoftAuthorizationStore(hasWorkflowAuthorization),
            resyncTrigger ?? new FakeInvoiceRecordResyncTrigger(),
            timeProvider ?? new FixedTimeProvider(new DateTimeOffset(2026, 9, 3, 14, 32, 31, TimeSpan.Zero)),
            Options.Create(new FreeAgentOptions { Environment = FreeAgentEnvironment.Sandbox, Subdomain = freeAgentSubdomain }));

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
            this.result = result ?? new ExpectedRecordGenerationTriggered();
        }

        public bool WasTriggered { get; private set; }

        public Task<ExpectedRecordGenerationTriggerResult> TriggerAsync(CancellationToken cancellationToken)
        {
            WasTriggered = true;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeInvoiceRecordResyncTrigger(InvoiceRecordResyncTriggerResult? result = null)
        : IInvoiceRecordResyncTrigger
    {
        public bool WasTriggered { get; private set; }
        public bool LastConfirmed { get; private set; }

        public Task<InvoiceRecordResyncTriggerResult> TriggerAsync(
            InvoiceConfigurationId configurationId,
            IntegrationType integrationType,
            InvoiceConfigurationActor actor,
            bool confirmed,
            CancellationToken cancellationToken)
        {
            WasTriggered = true;
            LastConfirmed = confirmed;
            return Task.FromResult(
                result ?? new InvoiceRecordResyncTriggerSucceeded(InvoiceRecordId.NewId(new DateOnly(2025, 7, 1), configurationId)));
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
