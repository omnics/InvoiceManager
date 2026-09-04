using InvoiceManager.AdminWeb.Services;
using InvoiceManager.Core;
using InvoiceManager.Infrastructure;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.Infrastructure.MicrosoftAuthorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace InvoiceManager.AdminWeb.Pages;

public class AuthorizationModel : PageModel
{
    private readonly IMicrosoftAuthorizationStore authorizationStore;
    private readonly MicrosoftAuthorizationOptions authorizationOptions;
    private readonly KeyVaultOptions keyVaultOptions;
    private readonly IFreeAgentAuthorizationStore freeAgentAuthorizationStore;
    private readonly FreeAgentAuthorizationOptions freeAgentAuthorizationOptions;

    public AuthorizationModel(
        IMicrosoftAuthorizationStore authorizationStore,
        IOptions<MicrosoftAuthorizationOptions> authorizationOptions,
        IOptions<KeyVaultOptions> keyVaultOptions,
        IFreeAgentAuthorizationStore freeAgentAuthorizationStore,
        IOptions<FreeAgentAuthorizationOptions> freeAgentAuthorizationOptions)
    {
        this.authorizationStore = authorizationStore;
        this.authorizationOptions = authorizationOptions.Value;
        this.keyVaultOptions = keyVaultOptions.Value;
        this.freeAgentAuthorizationStore = freeAgentAuthorizationStore;
        this.freeAgentAuthorizationOptions = freeAgentAuthorizationOptions.Value;
    }

    public bool IsSignedIn { get; private set; }

    public string DisplayName { get; private set; } = "Not signed in";

    public bool IsAuthorizationCaptured { get; private set; }

    public bool CanAuthorize { get; private set; }

    public bool ShowAuthorizeButton => CanAuthorize;

    public string AuthorizeButtonCaption
    {
        get
        {
            return IsAuthorizationCaptured
                ? "Replace Microsoft authorization"
                : "Capture Microsoft authorization";
        }
    }

    public string? StatusMessage { get; private set; }

    public IReadOnlyList<string> ConfigurationMessages { get; private set; } = [];

    public bool IsFreeAgentAuthorizationCaptured { get; private set; }

    /// <summary>
    /// A refresh token is stored, but no subdomain - either authorization was captured before
    /// this capture existed, or a prior attempt to save the subdomain failed. The bill link on
    /// the home dashboard stays disabled either way until this is resolved by re-authorizing.
    /// </summary>
    public bool IsFreeAgentSubdomainMissing { get; private set; }

    public bool CanAuthorizeFreeAgent { get; private set; }

    public bool ShowFreeAgentAuthorizeButton => CanAuthorizeFreeAgent;

    public string FreeAgentAuthorizeButtonCaption
    {
        get
        {
            return IsFreeAgentAuthorizationCaptured
                ? "Replace FreeAgent authorization"
                : "Capture FreeAgent authorization";
        }
    }

    public string? FreeAgentStatusMessage { get; private set; }

    public IReadOnlyList<string> FreeAgentConfigurationMessages { get; private set; } = [];

    public async Task OnGetAsync(string? status = null)
    {
        await LoadPageStateAsync(status);
    }

    public IActionResult OnPostAuthorize(bool confirmed)
    {
        if (!confirmed)
        {
            TempData["StatusMessage"] = "Confirm that you intend to replace the unattended workflow account.";
            return RedirectToPage();
        }

        var configurationMessages = GetConfigurationMessages();
        if (configurationMessages.Count > 0)
        {
            TempData["StatusMessage"] = configurationMessages[0];
            return RedirectToPage();
        }

        var redirectUri = Url.Page("/Authorization", null, new { status = "authorized" })
            ?? "/Authorization";
        return Challenge(
            new AuthenticationProperties { RedirectUri = redirectUri },
            MicrosoftOpenIdConnectOptionsSetup.WorkflowAuthorizationScheme);
    }

    public async Task<IActionResult> OnPostResetAsync()
    {
        await authorizationStore.ClearTokenCacheAsync(HttpContext.RequestAborted);
        TempData["StatusMessage"] = "Microsoft authorization was reset.";
        return RedirectToPage();
    }

    public IActionResult OnPostSignOut()
    {
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public IActionResult OnPostAuthorizeFreeAgent(bool confirmed)
    {
        if (!confirmed)
        {
            TempData["FreeAgentStatusMessage"] = "Confirm that you intend to replace the FreeAgent account used by the unattended workflow.";
            return RedirectToPage();
        }

        var configurationMessages = GetFreeAgentConfigurationMessages();
        if (configurationMessages.Count > 0)
        {
            TempData["FreeAgentStatusMessage"] = configurationMessages[0];
            return RedirectToPage();
        }

        var redirectUri = Url.Page("/Authorization", null, new { status = "freeagent-authorized" })
            ?? "/Authorization";
        return Challenge(
            new AuthenticationProperties { RedirectUri = redirectUri },
            FreeAgentOAuthOptionsSetup.WorkflowAuthorizationScheme);
    }

    public async Task<IActionResult> OnPostResetFreeAgentAsync()
    {
        // Clears the subdomain before the refresh token (see FreeAgentAuthorizationCapture) -
        // otherwise a failure between the two clears could leave the subdomain outliving the
        // token that was supposed to own it, still usable for "Open FreeAgent bill" links even
        // though this page would report FreeAgent as no longer authorized.
        await FreeAgentAuthorizationCapture.ClearAsync(freeAgentAuthorizationStore, HttpContext.RequestAborted);
        TempData["FreeAgentStatusMessage"] = "FreeAgent authorization was reset.";
        return RedirectToPage();
    }

    private async Task LoadPageStateAsync(string? status)
    {
        IsSignedIn = User.Identity?.IsAuthenticated == true;
        DisplayName = IsSignedIn
            ? User.Identity?.Name ?? "Signed in"
            : "Not signed in";
        IsAuthorizationCaptured = await authorizationStore.HasTokenCacheAsync(HttpContext.RequestAborted);

        ConfigurationMessages = GetConfigurationMessages();
        CanAuthorize = ConfigurationMessages.Count == 0;

        StatusMessage = TempData["StatusMessage"] as string;
        if (status == "authorized")
        {
            StatusMessage = "Microsoft authorization was captured.";
        }

        IsFreeAgentAuthorizationCaptured = await freeAgentAuthorizationStore.HasRefreshTokenAsync(HttpContext.RequestAborted);
        IsFreeAgentSubdomainMissing = IsFreeAgentAuthorizationCaptured
            && await freeAgentAuthorizationStore.ReadSubdomainAsync(HttpContext.RequestAborted) is None;

        FreeAgentConfigurationMessages = GetFreeAgentConfigurationMessages();
        CanAuthorizeFreeAgent = FreeAgentConfigurationMessages.Count == 0;

        FreeAgentStatusMessage = TempData["FreeAgentStatusMessage"] as string;
        if (status == "freeagent-authorized")
        {
            FreeAgentStatusMessage = "FreeAgent authorization was captured.";
        }
    }

    private List<string> GetConfigurationMessages()
    {
        var messages = new List<string>();

        if (!authorizationOptions.HasEntraConfiguration)
        {
            messages.Add("Set MicrosoftAuthorization:TenantId and MicrosoftAuthorization:ClientId before authorizing Microsoft.");
        }

        if (!authorizationOptions.HasClientSecret)
        {
            messages.Add("Set MicrosoftAuthorization:ClientSecret before authorizing Microsoft.");
        }

        if (!keyVaultOptions.HasPersistentStore)
        {
            messages.Add("Set KeyVault:Uri before captured authorization can be saved.");
        }

        return messages;
    }

    private List<string> GetFreeAgentConfigurationMessages()
    {
        var messages = new List<string>();

        if (!freeAgentAuthorizationOptions.HasClientConfiguration)
        {
            messages.Add("Set FreeAgentAuthorization:ClientId and FreeAgentAuthorization:ClientSecret before authorizing FreeAgent.");
        }

        if (!keyVaultOptions.HasPersistentStore)
        {
            messages.Add("Set KeyVault:Uri before captured authorization can be saved.");
        }

        return messages;
    }
}
