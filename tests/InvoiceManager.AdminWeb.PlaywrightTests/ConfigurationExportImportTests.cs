using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace InvoiceManager.AdminWeb.PlaywrightTests;

// End-to-end coverage for issue #81: export a configuration to a file from the Index page, then
// import that same file to create a new (inactive) configuration, confirming the round trip
// through real Save/Build validation rather than any shortcut around it.
[Collection("AdminWebAppHost")]
[Trait("Category", "Integration")]
public sealed class ConfigurationExportImportTests(AdminWebAppHostFixture appHost)
{
    [Fact]
    public async Task ExportThenImport_GraphEmailConfiguration_CreatesNewInactiveDraft()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var sourceDescription = $"Playwright Export Source {uniqueSuffix}";
        var sourceId = $"export-source-{uniqueSuffix}";

        // Create the configuration to be exported.
        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/Configurations/Create").ToString());
        await page.Locator("#Input_IntegrationType").SelectOptionAsync("GraphEmail");
        await page.Locator("#Input_InvoiceDescription").FillAsync(sourceDescription);
        // Set after the description fill: site.js auto-slugs #Input_Id from the description on
        // every "input" event until the Id field itself fires a "change" (i.e. a real edit), so
        // setting it any earlier would just get silently overwritten by the description fill above.
        await page.EvalOnSelectorAsync("#Input_Id", "(el, v) => el.value = v", sourceId);
        await page.Locator("#Input_SenderEmailAddress").FillAsync("billing@example.com");
        await page.Locator("#Input_BodyPattern").FillAsync("Invoice \\d+");
        await page.EvalOnSelectorAsync("#Input_DriveId", "(el, v) => el.value = v", TestOneDriveFolder.DriveId);
        await page.EvalOnSelectorAsync("#Input_DriveName", "(el, v) => el.value = v", TestOneDriveFolder.DriveName);
        await page.EvalOnSelectorAsync("#Input_FolderItemId", "(el, v) => el.value = v", TestOneDriveFolder.FolderItemId);
        await page.EvalOnSelectorAsync("#Input_FolderPath", "el => el.value = '/Bills'");
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Save configuration" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Configurations$"));

        // Export it: intercept the download rather than writing to disk.
        var configurationRow = page.Locator("section.status-panel", new PageLocatorOptions { HasText = sourceDescription });
        var downloadTask = page.WaitForDownloadAsync();
        await configurationRow.Locator("a", new LocatorLocatorOptions { HasText = "Export" }).ClickAsync();
        var download = await downloadTask;
        var exportPath = await download.PathAsync();
        Assert.NotNull(exportPath);
        var json = await File.ReadAllTextAsync(exportPath!);
        using var exportDocument = JsonDocument.Parse(json);
        var exportedProperties = exportDocument.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(sourceId, exportDocument.RootElement.GetProperty("id").GetString());
        Assert.DoesNotContain("documentType", exportedProperties);
        Assert.DoesNotContain("partitionKey", exportedProperties);
        Assert.DoesNotContain("_etag", exportedProperties);
        Assert.DoesNotContain("isActive", exportedProperties);

        // Import it back as a new configuration under a different ID.
        var importedId = $"{sourceId}-imported";
        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/Configurations/Import").ToString());
        await page.SetInputFilesAsync("input[type=file]", exportPath!);
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Load file" }).ClickAsync();

        await Assertions.Expect(page.Locator("#Input_InvoiceDescription")).ToHaveValueAsync(sourceDescription);
        await page.EvalOnSelectorAsync("#Input_Id", "(el, v) => el.value = v", importedId);
        // A copy with identical search criteria (same sender + body pattern) as the still-active
        // source configuration would be rejected as a duplicate match - give the imported copy its
        // own body pattern so this test is exercising the round trip, not the duplicate check
        // (which has its own dedicated test below).
        await page.Locator("#Input_BodyPattern").FillAsync($"Invoice \\d+ {uniqueSuffix}");
        // The imported OneDrive folder happens to still verify against Graph (same test folder),
        // so no re-pick is required here for the save to succeed — Build()/ResolveFolderAsync
        // re-verify it regardless. The confirmation checkbox is still required though: it exists
        // precisely so a folder that merely *resolves* in this environment (but is the wrong one,
        // e.g. carried over from dev/staging) can't be saved without an explicit human check.
        await page.Locator("#ConfirmedFolderSelection").CheckAsync();
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Save configuration" }).ClickAsync();

        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Configurations$"));
        await Assertions.Expect(page.Locator("body")).ToContainTextAsync(sourceDescription);
    }

    [Fact]
    public async Task Import_AutoSuffixesId_WhenImportedIdAlreadyExists()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var sourceDescription = $"Playwright Suffix Source {uniqueSuffix}";
        var sourceId = $"suffix-source-{uniqueSuffix}";

        // Create the configuration to be exported.
        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/Configurations/Create").ToString());
        await page.Locator("#Input_IntegrationType").SelectOptionAsync("GraphEmail");
        await page.Locator("#Input_InvoiceDescription").FillAsync(sourceDescription);
        await page.EvalOnSelectorAsync("#Input_Id", "(el, v) => el.value = v", sourceId);
        await page.Locator("#Input_SenderEmailAddress").FillAsync("billing@example.com");
        await page.Locator("#Input_BodyPattern").FillAsync($"Invoice {uniqueSuffix} \\d+");
        await page.EvalOnSelectorAsync("#Input_DriveId", "(el, v) => el.value = v", TestOneDriveFolder.DriveId);
        await page.EvalOnSelectorAsync("#Input_DriveName", "(el, v) => el.value = v", TestOneDriveFolder.DriveName);
        await page.EvalOnSelectorAsync("#Input_FolderItemId", "(el, v) => el.value = v", TestOneDriveFolder.FolderItemId);
        await page.EvalOnSelectorAsync("#Input_FolderPath", "el => el.value = '/Bills'");
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Save configuration" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Configurations$"));

        // Export it, then re-import the exact same file - the Configuration ID in the file
        // collides with the one just created, so the Import page should silently suffix it with
        // "-1" during the upload step, rather than surfacing a validation error on that ID.
        var configurationRow = page.Locator("section.status-panel", new PageLocatorOptions { HasText = sourceDescription });
        var downloadTask = page.WaitForDownloadAsync();
        await configurationRow.Locator("a", new LocatorLocatorOptions { HasText = "Export" }).ClickAsync();
        var download = await downloadTask;
        var exportPath = await download.PathAsync();
        Assert.NotNull(exportPath);

        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/Configurations/Import").ToString());
        await page.SetInputFilesAsync("input[type=file]", exportPath!);
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Load file" }).ClickAsync();

        await Assertions.Expect(page.Locator("#Input_Id")).ToHaveValueAsync($"{sourceId}-1");
    }

    [Fact]
    public async Task Import_RejectsSaveWhenSearchCriteriaMatchesAnExistingConfiguration()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var sourceDescription = $"Playwright Duplicate Source {uniqueSuffix}";
        var sourceId = $"dup-source-{uniqueSuffix}";

        // Create the configuration to be exported.
        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/Configurations/Create").ToString());
        await page.Locator("#Input_IntegrationType").SelectOptionAsync("GraphEmail");
        await page.Locator("#Input_InvoiceDescription").FillAsync(sourceDescription);
        await page.EvalOnSelectorAsync("#Input_Id", "(el, v) => el.value = v", sourceId);
        await page.Locator("#Input_SenderEmailAddress").FillAsync("billing@example.com");
        await page.Locator("#Input_BodyPattern").FillAsync($"Invoice {uniqueSuffix} \\d+");
        await page.EvalOnSelectorAsync("#Input_DriveId", "(el, v) => el.value = v", TestOneDriveFolder.DriveId);
        await page.EvalOnSelectorAsync("#Input_DriveName", "(el, v) => el.value = v", TestOneDriveFolder.DriveName);
        await page.EvalOnSelectorAsync("#Input_FolderItemId", "(el, v) => el.value = v", TestOneDriveFolder.FolderItemId);
        await page.EvalOnSelectorAsync("#Input_FolderPath", "el => el.value = '/Bills'");
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Save configuration" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Configurations$"));

        // Export it, then import it back under a different ID but with identical search criteria
        // (same sender + body pattern) and a different destination folder - saving should be
        // rejected as a duplicate match rather than silently creating a second configuration that
        // would pick up the same source files.
        var configurationRow = page.Locator("section.status-panel", new PageLocatorOptions { HasText = sourceDescription });
        var downloadTask = page.WaitForDownloadAsync();
        await configurationRow.Locator("a", new LocatorLocatorOptions { HasText = "Export" }).ClickAsync();
        var download = await downloadTask;
        var exportPath = await download.PathAsync();
        Assert.NotNull(exportPath);

        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/Configurations/Import").ToString());
        await page.SetInputFilesAsync("input[type=file]", exportPath!);
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Load file" }).ClickAsync();
        // Wait for the loaded form before editing it; otherwise the edit can race the post-back navigation.
        await Assertions.Expect(page.Locator("#ConfirmedFolderSelection")).ToBeVisibleAsync();
        await page.EvalOnSelectorAsync("#Input_Id", "(el, v) => el.value = v", $"{sourceId}-imported");
        await page.Locator("#ConfirmedFolderSelection").CheckAsync();
        await page.Locator("button[type=submit]", new PageLocatorOptions { HasText = "Save configuration" }).ClickAsync();

        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Configurations/Import$"));
        await Assertions.Expect(page.Locator(".notice.warning"))
            .ToContainTextAsync("already has the same search criteria");
    }
}
