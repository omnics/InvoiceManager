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
        InvoiceConfigurationId configurationId,
        IntegrationType integrationType,
        InvoiceConfigurationActor actor,
        bool confirmed,
        CancellationToken cancellationToken);
}

/// <summary>The resync refreshed the record's snapshot and reset it to Expected.</summary>
public sealed record InvoiceRecordResyncTriggerSucceeded(InvoiceRecordId RecordId);

/// <summary>The configuration has no record yet, so there was nothing to resync.</summary>
public sealed record InvoiceRecordResyncTriggerNoRecordExists;

/// <summary>No configuration with the given ID/integration type exists.</summary>
public sealed record InvoiceRecordResyncTriggerConfigurationNotFound;

/// <summary>The configuration's most recent record has already progressed past matching, so it was not resynced.</summary>
public sealed record InvoiceRecordResyncTriggerNotEligible(InvoiceRecordId RecordId);

/// <summary>
/// The record is eligible, but resyncing it would supersede a pending administrator decision and
/// the caller did not pass <c>confirmed: true</c> - checked by the Functions app against the same
/// record instance it was about to mutate, not the caller's possibly-stale read.
/// </summary>
public sealed record InvoiceRecordResyncTriggerConfirmationRequired(InvoiceRecordId RecordId);

/// <summary>No Functions base URL was configured, so no request could be made.</summary>
public sealed record InvoiceRecordResyncNotConfigured;

/// <summary>
/// A request was made but the Functions app was unreachable, returned a non-success status,
/// or reported an outcome this client does not recognise.
/// </summary>
public sealed record InvoiceRecordResyncFailed(string Message);

/// <summary>
/// Outcome of asking the Functions app to resync a configuration's most recent invoice
/// record. Modelled as a union - mirroring <see cref="InvoiceRecordResyncResult"/> on the
/// Functions side - so callers cannot forget to handle a failure mode, and an impossible
/// combination (for example a "succeeded" outcome with no record ID) is unrepresentable.
/// </summary>
public union InvoiceRecordResyncTriggerResult(
    InvoiceRecordResyncTriggerSucceeded,
    InvoiceRecordResyncTriggerNoRecordExists,
    InvoiceRecordResyncTriggerConfigurationNotFound,
    InvoiceRecordResyncTriggerNotEligible,
    InvoiceRecordResyncTriggerConfirmationRequired,
    InvoiceRecordResyncNotConfigured,
    InvoiceRecordResyncFailed);

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
        InvoiceConfigurationId configurationId,
        IntegrationType integrationType,
        InvoiceConfigurationActor actor,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        var functionsBaseUrl = configuration.GetValue<Uri?>("Functions:BaseUrl");
        if (functionsBaseUrl is null)
        {
            return new InvoiceRecordResyncNotConfigured();
        }

        var triggerUri = new Uri(
            functionsBaseUrl,
            $"{TriggerPath}?configurationId={Uri.EscapeDataString(configurationId.Value)}&integrationType={integrationType}" +
            $"&actorObjectId={Uri.EscapeDataString(actor.ObjectId)}&actorDisplayName={Uri.EscapeDataString(actor.DisplayName)}" +
            $"&confirmed={confirmed}");
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
            return ToResult(body);
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

    // Maps the wire DTO (a bare string/nullable-ID pair, the only shape JSON can carry) to the
    // typed result above - the one place an unrecognised or malformed outcome is possible, and
    // therefore the one place it is handled, rather than leaking as an unrepresentable state
    // into every caller.
    private static InvoiceRecordResyncTriggerResult ToResult(ResyncResultDto body) => body switch
    {
        { Outcome: "Succeeded", RecordId: { } id } => new InvoiceRecordResyncTriggerSucceeded(new InvoiceRecordId(id)),
        { Outcome: "NoRecordExists" } => new InvoiceRecordResyncTriggerNoRecordExists(),
        { Outcome: "ConfigurationNotFound" } => new InvoiceRecordResyncTriggerConfigurationNotFound(),
        { Outcome: "NotEligible", RecordId: { } id } => new InvoiceRecordResyncTriggerNotEligible(new InvoiceRecordId(id)),
        { Outcome: "ConfirmationRequired", RecordId: { } id } =>
            new InvoiceRecordResyncTriggerConfirmationRequired(new InvoiceRecordId(id)),
        _ => new InvoiceRecordResyncFailed($"The Functions app returned an unrecognised resync result: {body}."),
    };

    private sealed record ResyncResultDto(string Outcome, string? RecordId);
}
