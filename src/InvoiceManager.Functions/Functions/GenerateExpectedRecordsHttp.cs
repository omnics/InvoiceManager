using System.Net;
using System.Text.Json;
using InvoiceManager.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace InvoiceManager.Functions.Functions;

public sealed class GenerateExpectedRecordsHttp(
    ExpectedRecordGenerator generator,
    DueInvoiceProcessor processor,
    ILogger<GenerateExpectedRecordsHttp> logger)
{
    private const HttpStatusCode MultiStatus = (HttpStatusCode)207;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Function("GenerateExpectedRecordsHttp")]
    public async Task<HttpResponseData> RunAsync(
        // Anonymous at the host level: App Service Authentication (Easy Auth, Entra ID)
        // sits in front and rejects unauthenticated callers with 401, and the app
        // registration's "Invoke" role is assignment-required, so only the AdminWeb
        // managed identity and the named operator can obtain a token to reach this.
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Expected record generation triggered by HTTP request.");
        var generationResults = await generator.GenerateForAllActiveAsync(cancellationToken);
        logger.LogInformation(
            "Expected record generation complete: {SucceededCount} succeeded, {FailedCount} failed.",
            generationResults.Count(r => r is GenerationSucceeded),
            generationResults.Count(r => r is GenerationFailed));

        var processingResults = await processor.ProcessDueAsync(cancellationToken);
        logger.LogInformation(
            "Due invoice processing complete: {SavedCount} saved, {ReconciledCount} reconciled, " +
            "{NoMatchCount} no match yet, {NotFoundCount} not found, {FailedCount} failed, " +
            "{FreeAgentAmbiguousCount} FreeAgent ambiguous, {FreeAgentInterventionCount} FreeAgent intervention required, " +
            "{FreeAgentConflictCount} FreeAgent conflict.",
            processingResults.Count(r => r is ProcessingSucceeded),
            processingResults.Count(r => r is ProcessingReconciled),
            processingResults.Count(r => r is ProcessingNoMatch),
            processingResults.Count(r => r is ProcessingNotFound),
            processingResults.Count(r => r is ProcessingFailed),
            processingResults.Count(r => r is ProcessingFreeAgentAmbiguous),
            processingResults.Count(r => r is ProcessingFreeAgentInterventionRequired),
            processingResults.Count(r => r is ProcessingFreeAgentConflict));

        var body = new ExpectedRecordGenerationRunWire(
            generationResults.Select(result => result switch
            {
                GenerationSucceeded succeeded => new ConfigurationOutcomeWire(succeeded.ConfigurationId.Value, "Succeeded", null),
                GenerationFailed failed => new ConfigurationOutcomeWire(failed.ConfigurationId.Value, "Failed", failed.Exception.Message),
            }).ToList(),
            processingResults.Select(result => result switch
            {
                ProcessingSucceeded saved => new RecordOutcomeWire(saved.RecordId.Value, "SavedToOneDrive", null),
                ProcessingReconciled reconciled => new RecordOutcomeWire(reconciled.RecordId.Value, "ReconciledFromOneDrive", null),
                ProcessingNoMatch noMatch => new RecordOutcomeWire(noMatch.RecordId.Value, "NoMatch", null),
                ProcessingNotFound notFound => new RecordOutcomeWire(notFound.RecordId.Value, "NotFound", null),
                ProcessingFailed failed => new RecordOutcomeWire(failed.RecordId.Value, "Failed", failed.Exception.Message),
                ProcessingFreeAgentAmbiguous ambiguous => new RecordOutcomeWire(ambiguous.RecordId.Value, "FreeAgentAmbiguous", null),
                ProcessingFreeAgentInterventionRequired intervention => new RecordOutcomeWire(
                    intervention.RecordId.Value, "FreeAgentInterventionRequired", null),
                ProcessingFreeAgentConflict conflict => new RecordOutcomeWire(conflict.RecordId.Value, "FreeAgentConflict", conflict.Reason),
            }).ToList());

        // Set the status as part of the write: under the ASP.NET Core integration
        // WriteAsJsonAsync starts the response, so assigning StatusCode afterwards throws
        // "response has already started".
        // Set the status at creation and write the body ourselves: under the ASP.NET Core
        // integration WriteAsJsonAsync starts the response and forces 200, so the status
        // must be fixed before the first write rather than overridden after it.
        var response = req.CreateResponse(MultiStatus);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, SerializerOptions), cancellationToken);
        return response;
    }
}
