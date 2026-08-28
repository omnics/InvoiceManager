using System.Text.Json;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Core.Repositories;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceManager.AdminWeb.Pages.Configurations;

public sealed class IndexModel(
    InvoiceConfigurationService service,
    IMicrosoftAuthorizationStore authorizationStore,
    IMicrosoftResourceDiscovery discovery,
    IInvoiceRecordRepository recordRepository,
    IInvoiceRecordResyncTrigger resyncTrigger) : PageModel
{
    public IReadOnlyList<StoredInvoiceConfiguration> Configurations { get; private set; } = [];
    public bool HasWorkflowAuthorization { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool EditingEnabled => HasWorkflowAuthorization;

    // Billing account id -> display name only (the ID itself is shown separately, behind a
    // "Show ID" disclosure, rather than baked into this label). Best-effort: falls back to
    // displaying the raw ID (via GetValueOrDefault in the view) if discovery fails or an
    // account isn't found.
    public IReadOnlyDictionary<string, string> BillingAccountLabels { get; private set; } =
        new Dictionary<string, string>();

    public async Task OnGetAsync()
    {
        await LoadAsync();
        StatusMessage = TempData["StatusMessage"] as string;
    }

    public async Task<IActionResult> OnPostSetActiveAsync(
        string id,
        IntegrationType integrationType,
        string etag,
        bool activate)
    {
        if (!await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted))
        {
            TempData["StatusMessage"] = "Capture Microsoft authorization before changing configuration state.";
            return RedirectToPage();
        }
        var current = await service.GetAsync(new(id), integrationType, HttpContext.RequestAborted);
        if (current is not StoredInvoiceConfiguration stored)
            return NotFound();
        var result = await service.SetActiveAsync(
            stored with { ETag = etag }, activate, User.ToConfigurationActor(), HttpContext.RequestAborted);
        TempData["StatusMessage"] = InvoiceConfigurationMutationErrorMessages.TryGetMessage(result) is string message
            ? message
            : activate
                ? "Configuration activated. Processing will occur on the next scheduled or manual workflow run."
                : "Configuration deactivated. Outstanding records are preserved and will be skipped while it is inactive.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResyncStuckRecordAsync(
        string id, IntegrationType integrationType, bool confirmed)
    {
        var configurationId = new InvoiceConfigurationId(id);
        if (await recordRepository.GetMostRecentAsync(configurationId, HttpContext.RequestAborted) is InvoiceRecord current &&
            InvoiceRecordResync.RequiresConfirmation(current.State) && !confirmed)
        {
            TempData["StatusMessage"] =
                "This resync would supersede a pending Guess-removal intervention without a decision being " +
                "recorded. Confirm before continuing.";
            return RedirectToPage();
        }

        var result = await resyncTrigger.TriggerAsync(
            configurationId, integrationType, User.ToConfigurationActor(), HttpContext.RequestAborted);
        TempData["StatusMessage"] = result switch
        {
            InvoiceRecordResyncTriggerSucceeded =>
                "The most recent record was refreshed from the current configuration and reset to Expected; it will " +
                "be retried the next time this configuration is processed (skipped while it is inactive).",
            InvoiceRecordResyncTriggerNoRecordExists =>
                "This configuration has no record yet, so there is nothing to resync.",
            InvoiceRecordResyncTriggerNotEligible =>
                "The most recent record has already progressed past matching, so it was not resynced.",
            InvoiceRecordResyncTriggerConfigurationNotFound => "Configuration not found.",
            InvoiceRecordResyncNotConfigured =>
                "The Functions app URL is not configured, so the resync could not be triggered.",
            InvoiceRecordResyncFailed failed => $"The resync could not be triggered. {failed.Message}",
        };
        return RedirectToPage();
    }

    /// <summary>
    /// Downloads a single configuration as a JSON file for import into another environment. Built
    /// from the domain model (<see cref="InvoiceConfigurationExport.FromConfiguration"/>), never
    /// from the Cosmos document, so Cosmos-internal fields never appear in the file. Available
    /// without workflow authorization, matching the rest of this page's read-only access.
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync(string id, IntegrationType integrationType)
    {
        var current = await service.GetAsync(new(id), integrationType, HttpContext.RequestAborted);
        if (current is not StoredInvoiceConfiguration stored) return NotFound();
        var export = InvoiceConfigurationExport.FromConfiguration(stored.Configuration);
        var json = JsonSerializer.SerializeToUtf8Bytes(export, InvoiceConfigurationExportJson.Options);
        return File(json, "application/json", $"{stored.Configuration.Id.Value}.invoiceconfiguration.json");
    }

    private async Task LoadAsync()
    {
        Configurations = await service.ListAsync(HttpContext.RequestAborted);
        HasWorkflowAuthorization = await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted);

        if (Configurations.Any(c => c.Configuration.IntegrationType == IntegrationType.MicrosoftBilling))
        {
            try
            {
                var accounts = await discovery.ListBillingAccountsAsync(HttpContext.RequestAborted);
                BillingAccountLabels = accounts.ToDictionary(
                    a => a.Id, a => string.IsNullOrWhiteSpace(a.DisplayName) ? a.Id : a.DisplayName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort only: the list still renders with raw billing account IDs.
            }
        }
    }
}
