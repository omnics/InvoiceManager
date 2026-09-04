using System.Net;
using System.Text;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using InvoiceManager.TestSupport;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class FreeAgentCompanyLookupTests
{
    [Fact]
    public async Task GetCompanyAsync_ParsesTheSubdomainFromASnakeCaseCompanyResponse()
    {
        var handler = new StubHttpMessageHandler((request, index) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"company": {"subdomain": "omnicssandbox", "name": "Omnics Sandbox"}}""",
                Encoding.UTF8, "application/json"),
        });
        var httpClient = new HttpClient(handler);
        var lookup = new FreeAgentCompanyLookup(httpClient);

        var company = await lookup.GetCompanyAsync("access-token", FreeAgentEnvironment.Sandbox);

        Assert.Equal("omnicssandbox", company.Subdomain);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.sandbox.freeagent.com/v2/company", request.RequestUri?.ToString());
        Assert.Equal("Bearer access-token", request.Authorization);
    }

    [Fact]
    public async Task GetCompanyAsync_Throws_WhenTheResponseIsNotSuccessful()
    {
        var handler = new StubHttpMessageHandler((request, index) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var httpClient = new HttpClient(handler);
        var lookup = new FreeAgentCompanyLookup(httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lookup.GetCompanyAsync("access-token", FreeAgentEnvironment.Sandbox));
    }

    [Theory]
    [InlineData("""{"company": {"subdomain": ""}}""")]
    [InlineData("""{"company": {}}""")]
    [InlineData("""{}""")]
    public async Task GetCompanyAsync_Throws_WhenTheSubdomainIsMissingOrBlank(string responseBody)
    {
        var handler = new StubHttpMessageHandler((request, index) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });
        var httpClient = new HttpClient(handler);
        var lookup = new FreeAgentCompanyLookup(httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lookup.GetCompanyAsync("access-token", FreeAgentEnvironment.Sandbox));
    }
}
