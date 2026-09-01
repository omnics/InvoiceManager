using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceManager.AdminWeb.Pages;

/// <summary>
/// The three home dashboard columns an operator can sort by, clicking a column header.
/// </summary>
public enum InvoiceSyncSortColumn
{
    Configuration,
    Date,
    Status,
}

public class IndexModel(
    IExpectedRecordGenerationTrigger expectedRecordGenerationTrigger,
    InvoiceSyncOverview overview,
    IMicrosoftAuthorizationStore authorizationStore,
    IInvoiceRecordResyncTrigger resyncTrigger) : PageModel
{
    public IReadOnlyList<InvoiceSyncRow> Rows { get; private set; } = [];
    public bool HasWorkflowAuthorization { get; private set; }
    public string? StatusMessage { get; private set; }
    public InvoiceSyncSortColumn Sort { get; private set; }
    public bool SortDescending { get; private set; }

    /// <summary>
    /// Bound from the "sort"/"descending" query string on every request, GET or POST, so a
    /// resync/generate POST can echo the operator's current sort back into its redirect. Nullable
    /// so an absent query value is distinguishable from an explicit "not descending" - see
    /// <see cref="ResolveSort"/>.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "sort")]
    public InvoiceSyncSortColumn? SortParam { get; set; }

    [BindProperty(SupportsGet = true, Name = "descending")]
    public bool? DescendingParam { get; set; }

    public async Task OnGetAsync()
    {
        ResolveSort();
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
        return RedirectToCurrentSort();
    }

    public async Task<IActionResult> OnPostResyncRecordAsync(string id, IntegrationType integrationType, bool confirmed)
    {
        if (!await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted))
        {
            TempData["StatusMessage"] = "Capture Microsoft authorization before resyncing a record.";
            return RedirectToCurrentSort();
        }

        var result = await resyncTrigger.TriggerAsync(
            new(id), integrationType, User.ToConfigurationActor(), confirmed, HttpContext.RequestAborted);
        TempData["StatusMessage"] = result switch
        {
            InvoiceRecordResyncTriggerSucceeded =>
                "The most recent record was refreshed from the current configuration and reset to Expected; it will " +
                "be retried the next time this configuration is processed (skipped while it is inactive).",
            InvoiceRecordResyncTriggerNoRecordExists =>
                "This configuration has no record yet, so there is nothing to resync.",
            InvoiceRecordResyncTriggerNotEligible =>
                "The most recent record has already progressed past matching, so it was not resynced.",
            InvoiceRecordResyncTriggerConfirmationRequired =>
                "This resync would supersede a pending Guess-removal intervention without a decision being " +
                "recorded. Confirm before continuing.",
            InvoiceRecordResyncTriggerConfigurationNotFound => "Configuration not found.",
            InvoiceRecordResyncNotConfigured =>
                "The Functions app URL is not configured, so the resync could not be triggered.",
            InvoiceRecordResyncFailed failed => $"The resync could not be triggered. {failed.Message}",
        };
        return RedirectToCurrentSort();
    }

    /// <summary>
    /// Resolves <see cref="Sort"/>/<see cref="SortDescending"/> from the bound query values,
    /// defaulting to date descending (the dashboard's original default) when nothing was
    /// specified, and to ascending for a newly-selected non-date column.
    /// </summary>
    private void ResolveSort()
    {
        Sort = SortParam ?? InvoiceSyncSortColumn.Date;
        SortDescending = DescendingParam ?? (Sort == InvoiceSyncSortColumn.Date);
    }

    private IActionResult RedirectToCurrentSort()
    {
        ResolveSort();
        return RedirectToPage(new { sort = Sort, descending = SortDescending });
    }

    private async Task LoadAsync()
    {
        var rows = await overview.GetRowsAsync(HttpContext.RequestAborted);
        Rows = Sort switch
        {
            InvoiceSyncSortColumn.Configuration => SortDescending
                ? rows.OrderByDescending(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
                : rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            InvoiceSyncSortColumn.Status => SortDescending
                ? rows.OrderByDescending(r => r.Bucket).ToList()
                : rows.OrderBy(r => r.Bucket).ToList(),
            _ => SortDescending
                ? rows.OrderByDescending(r => r.Date).ToList()
                : rows.OrderBy(r => r.Date).ToList(),
        };
        HasWorkflowAuthorization = await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted);
    }
}
