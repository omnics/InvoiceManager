using System.Net;
using System.Net.Http.Headers;
using System.Text;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceManager.Integrations.FreeAgent.Tests;

/// <summary>
/// Verifies that IFreeAgentCompanyLookup, as registered by
/// <see cref="FreeAgentIntegrationRegistration.AddFreeAgentIntegration"/>, retries throttling
/// responses (429/503) via the standard resilience handler rather than failing the whole
/// authorization attempt on a single transient hit.
/// </summary>
public sealed class FreeAgentCompanyLookupResilienceTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetCompanyAsync_RetriesThrottling_ThroughTheStandardResilienceHandler(HttpStatusCode throttleStatus)
    {
        var handler = new StubHttpMessageHandler((_, index) => index == 0
            ? Throttled(throttleStatus)
            : Json(HttpStatusCode.OK, """{"company": {"subdomain": "acmeltd"}}"""));

        var services = new ServiceCollection();
        services.AddFreeAgentIntegration();
        services.AddHttpClient<IFreeAgentCompanyLookup, FreeAgentCompanyLookup>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<IFreeAgentCompanyLookup>();

        var company = await lookup.GetCompanyAsync("access-token", FreeAgentEnvironment.Sandbox);

        Assert.Equal("acmeltd", company.Subdomain.Value);
        // One throttled attempt, then a successful retry: proves the pipeline retried.
        Assert.Equal(2, handler.Requests.Count);
    }

    private static HttpResponseMessage Throttled(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("throttled") };
        // Retry immediately: honoured by the resilience handler, and keeps the test fast.
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
