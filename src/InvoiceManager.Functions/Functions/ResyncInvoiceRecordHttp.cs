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
        var configurationId = query["configurationId"];
        var integrationTypeText = query["integrationType"];

        if (string.IsNullOrWhiteSpace(configurationId) ||
            !Enum.TryParse<IntegrationType>(integrationTypeText, out var integrationType))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync(
                "Both 'configurationId' and a valid 'integrationType' query parameters are required.", cancellationToken);
            return badRequest;
        }

        logger.LogInformation(
            "Invoice record resync triggered by HTTP request for configuration {ConfigurationId}.", configurationId);

        var result = await resync.ResyncMostRecentAsync(
            new InvoiceConfigurationId(configurationId), integrationType, cancellationToken);

        var body = result switch
        {
            ResyncSucceeded succeeded => new ResyncResultDto("Succeeded", succeeded.RecordId.Value),
            ResyncConfigurationNotFound => new ResyncResultDto("ConfigurationNotFound", null),
            ResyncNoRecordExists => new ResyncResultDto("NoRecordExists", null),
            ResyncNotEligible notEligible => new ResyncResultDto("NotEligible", notEligible.RecordId.Value),
        };

        logger.LogInformation("Invoice record resync outcome for configuration {ConfigurationId}: {Outcome}.", configurationId, body.Outcome);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, SerializerOptions), cancellationToken);
        return response;
    }

    private sealed record ResyncResultDto(string Outcome, string? RecordId);
}
