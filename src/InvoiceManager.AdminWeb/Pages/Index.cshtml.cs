using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

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
    IInvoiceRecordResyncTrigger resyncTrigger,
    TimeProvider timeProvider,
    IOptions<FreeAgentOptions> freeAgentOptions) : PageModel
{
    public IReadOnlyList<InvoiceSyncRow> Rows { get; private set; } = [];
    public bool HasWorkflowAuthorization { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool StatusIsWarning { get; private set; }
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
        StatusIsWarning = TempData["StatusIsWarning"] as bool? ?? false;
    }

    public async Task<IActionResult> OnPostGenerateExpectedRecordsAsync()
    {
        // Captured before the call, not after: TriggerAsync doesn't return until the whole
        // (synchronous, potentially slow) Functions run has finished, so timing this afterwards
        // would report a completion time as the start time.
        var startedAt = timeProvider.GetUtcNow();
        var result = await expectedRecordGenerationTrigger.TriggerAsync(HttpContext.RequestAborted);
        var startedAtDisplay = $"{startedAt.UtcDateTime:HH:mm:ss} UTC";
        var (message, isWarning) = result switch
        {
            ExpectedRecordGenerationTriggered =>
                ($"Started invoice processing at {startedAtDisplay}.", false),
            ExpectedRecordGenerationCompletedWithErrors withErrors =>
                ($"Started invoice processing at {startedAtDisplay}, but {withErrors.Errors.Count} " +
                    $"item(s) failed: {string.Join("; ", withErrors.Errors)}", true),
            ExpectedRecordGenerationNotConfigured =>
                ("The Functions app URL is not configured, so invoice processing could not be started.", true),
            ExpectedRecordGenerationFailed failed =>
                ($"Invoice processing could not be started. {failed.Message}", true),
        };
        SetStatus(message, isWarning);
        return RedirectToCurrentSort();
    }

    public async Task<IActionResult> OnPostResyncRecordAsync(string id, IntegrationType integrationType, bool confirmed)
    {
        if (!await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted))
        {
            SetStatus("Capture Microsoft authorization before resyncing a record.", isWarning: true);
            return RedirectToCurrentSort();
        }

        var result = await resyncTrigger.TriggerAsync(
            new(id), integrationType, User.ToConfigurationActor(), confirmed, HttpContext.RequestAborted);
        var (message, isWarning) = result switch
        {
            InvoiceRecordResyncTriggerSucceeded =>
                ("The most recent record was refreshed from the current configuration and reset to Expected; it will " +
                "be retried the next time this configuration is processed (skipped while it is inactive).", false),
            InvoiceRecordResyncTriggerNoRecordExists =>
                ("This configuration has no record yet, so there is nothing to resync.", true),
            InvoiceRecordResyncTriggerNotEligible =>
                ("The most recent record has already progressed past matching, so it was not resynced.", true),
            InvoiceRecordResyncTriggerConfirmationRequired =>
                ("This resync would supersede a pending Guess-removal intervention without a decision being " +
                "recorded. Confirm before continuing.", true),
            InvoiceRecordResyncTriggerConfigurationNotFound => ("Configuration not found.", true),
            InvoiceRecordResyncNotConfigured =>
                ("The Functions app URL is not configured, so the resync could not be triggered.", true),
            InvoiceRecordResyncFailed failed => ($"The resync could not be triggered. {failed.Message}", true),
        };
        SetStatus(message, isWarning);
        return RedirectToCurrentSort();
    }

    /// <summary>
    /// The bill's link in FreeAgent's own web app, or null if <c>FreeAgent:Subdomain</c> isn't
    /// configured for this deployment - see <see cref="FreeAgentBillWebLinkExtensions.WebUrl"/>.
    /// </summary>
    public string? FreeAgentBillUrl(FreeAgentBillIdentity bill) =>
        bill.WebUrl(freeAgentOptions.Value) is Uri url ? url.ToString() : null;

    /// <summary>
    /// Whether <paramref name="location"/> is safe to render as an "Open file"/"Open folder"
    /// link - <see cref="OneDriveDetails.OneDriveLocation"/> falls back to a bare item ID (not a
    /// URL at all) when Graph didn't report a webUrl, and that must never be linked. Deliberately
    /// not <see cref="Uri.IsWellFormedUriString"/>, which is stricter than real SharePoint webUrls
    /// satisfy (e.g. an unencoded '+' in a folder name) and would reject perfectly good links.
    /// </summary>
    public bool IsHttpsUrl(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private void SetStatus(string message, bool isWarning)
    {
        TempData["StatusMessage"] = message;
        TempData["StatusIsWarning"] = isWarning;
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
