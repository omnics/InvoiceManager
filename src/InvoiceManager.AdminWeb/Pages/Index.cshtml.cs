using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Core.Repositories;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceManager.AdminWeb.Pages;

public class IndexModel(
    IExpectedRecordGenerationTrigger expectedRecordGenerationTrigger,
    InvoiceSyncOverview overview,
    IMicrosoftAuthorizationStore authorizationStore,
    IInvoiceRecordRepository recordRepository,
    IInvoiceRecordResyncTrigger resyncTrigger) : PageModel
{
    public IReadOnlyList<InvoiceSyncRow> Rows { get; private set; } = [];
    public bool HasWorkflowAuthorization { get; private set; }
    public string? StatusMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        StatusMessage = TempData["StatusMessage"] as string;
    }

    public async Task<IActionResult> OnPostGenerateExpectedRecordsAsync()
    {
        var result = await expectedRecordGenerationTrigger.TriggerAsync(HttpContext.RequestAborted);
        TempData["StatusMessage"] = result switch
        {
            ExpectedRecordGenerationTriggered triggered =>
                $"Expected record generation was triggered (HTTP {triggered.StatusCode}).",
            ExpectedRecordGenerationNotConfigured =>
                "The Functions app URL is not configured, so expected record generation could not be triggered.",
            ExpectedRecordGenerationFailed failed =>
                $"Expected record generation could not be triggered. {failed.Message}",
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResyncRecordAsync(string id, IntegrationType integrationType, bool confirmed)
    {
        if (!await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted))
        {
            TempData["StatusMessage"] = "Capture Microsoft authorization before resyncing a record.";
            return RedirectToPage();
        }

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

    private async Task LoadAsync()
    {
        Rows = await overview.GetRowsAsync(HttpContext.RequestAborted);
        HasWorkflowAuthorization = await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted);
    }
}
