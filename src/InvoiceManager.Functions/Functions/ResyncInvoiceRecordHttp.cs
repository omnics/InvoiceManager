using System.Net;
using System.Text.Json;
using InvoiceManager.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace InvoiceManager.Functions.Functions;

/// <summary>
/// Lets an operator recover a configuration's most recent invoice record after editing
/// its search criteria to match reality (for example a subscription price rise) - see
/// <see cref="InvoiceRecordResync"/> for why an <see cref="InvoiceConfiguration"/> edit
/// alone does not fix an already-generated record.
/// </summary>
public sealed class ResyncInvoiceRecordHttp(
    InvoiceRecordResync resync,
    ILogger<ResyncInvoiceRecordHttp> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Function("ResyncInvoiceRecordHttp")]
    public async Task<HttpResponseData> RunAsync(
        // Anonymous at the host level: see GenerateExpectedRecordsHttp for the Easy Auth
        // gate that actually protects this.
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        if (ParseRequest(
                query["configurationId"], query["integrationType"],
                query["actorObjectId"], query["actorDisplayName"], query["confirmed"])
            is not ParsedRequest parsed)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync(
                "'configurationId', a valid 'integrationType', 'actorObjectId', and 'actorDisplayName' query " +
                "parameters are all required.", cancellationToken);
            return badRequest;
        }

        var (configurationId, integrationType, actor, confirmed) = parsed;

        logger.LogInformation(
            "Invoice record resync triggered by HTTP request for configuration {ConfigurationId}.", configurationId);

        var result = await resync.ResyncMostRecentAsync(configurationId, integrationType, actor, confirmed, cancellationToken);

        var body = result switch
        {
            ResyncSucceeded succeeded => new ResyncResultDto("Succeeded", succeeded.RecordId.Value),
            ResyncConfigurationNotFound => new ResyncResultDto("ConfigurationNotFound", null),
            ResyncNoRecordExists => new ResyncResultDto("NoRecordExists", null),
            ResyncNotEligible notEligible => new ResyncResultDto("NotEligible", notEligible.RecordId.Value),
            ResyncConfirmationRequired confirmationRequired =>
                new ResyncResultDto("ConfirmationRequired", confirmationRequired.RecordId.Value),
        };

        logger.LogInformation("Invoice record resync outcome for configuration {ConfigurationId}: {Outcome}.", configurationId, body.Outcome);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, SerializerOptions), cancellationToken);
        return response;
    }

    /// <summary>
    /// Validates the request's query parameters, extracted for direct unit testing since
    /// <see cref="HttpRequestData"/> requires a full Functions host context to construct.
    /// Rejects an <paramref name="integrationTypeText"/> that parses to an undefined numeric
    /// value (for example "999") as well as a missing/unrecognised name - <see cref="Enum.TryParse{TEnum}(string?,out TEnum)"/>
    /// alone accepts any integer-parseable string for a non-flags enum, defined or not.
    /// </summary>
    public static Option<ParsedRequest> ParseRequest(
        string? configurationId,
        string? integrationTypeText,
        string? actorObjectId,
        string? actorDisplayName,
        string? confirmedText)
    {
        if (string.IsNullOrWhiteSpace(configurationId))
            return Option.None;

        if (!Enum.TryParse<IntegrationType>(integrationTypeText, out var integrationType) ||
            !Enum.IsDefined(integrationType))
        {
            return Option.None;
        }

        if (string.IsNullOrWhiteSpace(actorObjectId) || string.IsNullOrWhiteSpace(actorDisplayName))
            return Option.None;

        // Absent or unparseable is treated as "not confirmed" - a missing/malformed value must
        // never be silently read as consent to supersede a pending intervention.
        var confirmed = bool.TryParse(confirmedText, out var parsedConfirmed) && parsedConfirmed;

        return new ParsedRequest(
            new InvoiceConfigurationId(configurationId),
            integrationType,
            new InvoiceConfigurationActor(actorObjectId, actorDisplayName),
            confirmed);
    }

    public sealed record ParsedRequest(
        InvoiceConfigurationId ConfigurationId,
        IntegrationType IntegrationType,
        InvoiceConfigurationActor Actor,
        bool Confirmed);

    private sealed record ResyncResultDto(string Outcome, string? RecordId);
}
