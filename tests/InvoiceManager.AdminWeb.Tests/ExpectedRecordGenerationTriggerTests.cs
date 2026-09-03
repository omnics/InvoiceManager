using System.Net;
using Azure.Core;
using InvoiceManager.AdminWeb.Services;
using InvoiceManager.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvoiceManager.AdminWeb.Tests;

public sealed class ExpectedRecordGenerationTriggerTests
{
    [Fact]
    public async Task TriggerAsync_ReportsClean_WhenEveryOutcomeIsASuccessOrLegitimateRetry()
    {
        // NoMatch is a record still legitimately waiting on its tolerance window (see
        // ProcessingNoMatch), not a failure - it should not surface as an "error".
        var trigger = CreateTrigger("""
            {
                "generation": [{ "configurationId": "acme", "status": "Succeeded", "error": null }],
                "processing": [
                    { "recordId": "acme#2026-06-01", "status": "SavedToOneDrive", "error": null },
                    { "recordId": "acme#2026-07-01", "status": "NoMatch", "error": null }
                ]
            }
            """);

        var result = await trigger.TriggerAsync(CancellationToken.None);

        Assert.True(result is ExpectedRecordGenerationTriggered);
    }

    [Theory]
    [InlineData("NotFound")]
    [InlineData("FreeAgentAmbiguous")]
    [InlineData("FreeAgentInterventionRequired")]
    [InlineData("SomeFutureStatusThisBuildDoesNotKnowAbout")]
    public async Task TriggerAsync_SurfacesAsAnError_WhenAProcessingOutcomeNeedsAttentionButHasNoErrorMessage(
        string status)
    {
        // These statuses have a null "error" field (only Failed/FreeAgentConflict populate it),
        // but none of them are a clean outcome: NotFound is terminal, FreeAgentAmbiguous and
        // FreeAgentInterventionRequired are blocked pending the operator, and an unrecognised
        // status must never be silently treated as fine.
        var trigger = CreateTrigger($$"""
            {
                "generation": [],
                "processing": [{ "recordId": "acme#2026-06-01", "status": "{{status}}", "error": null }]
            }
            """);

        var result = await trigger.TriggerAsync(CancellationToken.None);

        Assert.True(result is ExpectedRecordGenerationCompletedWithErrors withErrors
            && withErrors.Errors.SequenceEqual([$"Record acme#2026-06-01: {status}"]));
    }

    [Fact]
    public async Task TriggerAsync_SurfacesTheErrorMessage_WhenOneIsPresent()
    {
        var trigger = CreateTrigger("""
            {
                "generation": [{ "configurationId": "acme", "status": "Failed", "error": "boom" }],
                "processing": []
            }
            """);

        var result = await trigger.TriggerAsync(CancellationToken.None);

        Assert.True(result is ExpectedRecordGenerationCompletedWithErrors withErrors
            && withErrors.Errors.SequenceEqual(["Configuration acme: boom"]));
    }

    private static FunctionsExpectedRecordGenerationTrigger CreateTrigger(string responseBody)
    {
        var handler = new StubHttpMessageHandler((request, index) => new HttpResponseMessage((HttpStatusCode)207)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Functions:BaseUrl", "https://functions.example.test")])
            .Build();
        return new FunctionsExpectedRecordGenerationTrigger(
            new HttpClient(handler),
            configuration,
            new NoOpTokenCredential(),
            NullLogger<FunctionsExpectedRecordGenerationTrigger>.Instance);
    }

    private sealed class NoOpTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
