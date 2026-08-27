using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using InvoiceManager.Core;

namespace InvoiceManager.AdminWeb.Services;

/// <summary>
/// Triggers the Functions app's <c>ResyncInvoiceRecordHttp</c> endpoint on behalf of an
/// operator. See <see cref="IExpectedRecordGenerationTrigger"/> for the equivalent pattern
/// and the reasoning behind AdminWeb calling into Functions rather than Cosmos directly.
/// </summary>
public interface IInvoiceRecordResyncTrigger
{
    Task<InvoiceRecordResyncTriggerResult> TriggerAsync(
        InvoiceConfigurationId configurationId, IntegrationType integrationType, CancellationToken cancellationToken);
}

/// <summary>The Functions endpoint ran the resync and reports what it did.</summary>
public sealed record InvoiceRecordResyncTriggered(string Outcome, string? RecordId);

/// <summary>No Functions base URL was configured, so no request could be made.</summary>
public sealed record InvoiceRecordResyncNotConfigured;

/// <summary>A request was made but the Functions app was unreachable or returned a non-success status.</summary>
public sealed record InvoiceRecordResyncFailed(string Message);

/// <summary>
/// Outcome of asking the Functions app to resync a configuration's most recent invoice
/// record. Modelled as a union so callers cannot forget to handle a failure mode.
/// </summary>
public union InvoiceRecordResyncTriggerResult(
    InvoiceRecordResyncTriggered, InvoiceRecordResyncNotConfigured, InvoiceRecordResyncFailed);

public sealed class FunctionsInvoiceRecordResyncTrigger(
    HttpClient httpClient,
    IConfiguration configuration,
    TokenCredential credential,
    ILogger<FunctionsInvoiceRecordResyncTrigger> logger)
    : IInvoiceRecordResyncTrigger
{
    private const string TriggerPath = "/api/ResyncInvoiceRecordHttp";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<InvoiceRecordResyncTriggerResult> TriggerAsync(
        InvoiceConfigurationId configurationId, IntegrationType integrationType, CancellationToken cancellationToken)
    {
        var functionsBaseUrl = configuration.GetValue<Uri?>("Functions:BaseUrl");
        if (functionsBaseUrl is null)
        {
            return new InvoiceRecordResyncNotConfigured();
        }

        var triggerUri = new Uri(
            functionsBaseUrl,
            $"{TriggerPath}?configurationId={Uri.EscapeDataString(configurationId.Value)}&integrationType={integrationType}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, triggerUri);
            await AuthorizeAsync(request, cancellationToken);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new InvoiceRecordResyncFailed(
                    $"The Functions app returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadFromJsonAsync<ResyncResultDto>(SerializerOptions, cancellationToken)
                ?? throw new InvalidOperationException("The Functions app returned an empty resync result.");
            return new InvoiceRecordResyncTriggered(body.Outcome, body.RecordId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Triggering an invoice record resync failed.");
            return new InvoiceRecordResyncFailed("The Functions app is not reachable.");
        }
    }

    // See FunctionsExpectedRecordGenerationTrigger.AuthorizeAsync for why this is conditional.
    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var scope = configuration.GetValue<string?>("Functions:Scope");
        if (string.IsNullOrWhiteSpace(scope))
        {
            return;
        }

        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private sealed record ResyncResultDto(string Outcome, string? RecordId);
}
