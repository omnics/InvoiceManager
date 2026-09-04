using InvoiceManager.Infrastructure.FreeAgentAuthorization;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class FreeAgentHostsTests
{
    [Theory]
    [InlineData(FreeAgentEnvironment.Sandbox, "api.sandbox.freeagent.com")]
    [InlineData(FreeAgentEnvironment.Production, "api.freeagent.com")]
    public void Host_ReturnsTheFixedHostForEachEnvironment(FreeAgentEnvironment environment, string expectedHost)
    {
        Assert.Equal(expectedHost, FreeAgentHosts.Host(environment));
    }

    [Theory]
    [InlineData(FreeAgentEnvironment.Sandbox)]
    [InlineData(FreeAgentEnvironment.Production)]
    public void ApiBaseUri_AuthorizationEndpoint_TokenEndpoint_AreDerivedFromTheSameHost(FreeAgentEnvironment environment)
    {
        var host = FreeAgentHosts.Host(environment);

        Assert.Equal($"https://{host}/v2/", FreeAgentHosts.ApiBaseUri(environment).ToString());
        Assert.Equal($"https://{host}/v2/approve_app", FreeAgentHosts.AuthorizationEndpoint(environment).ToString());
        Assert.Equal($"https://{host}/v2/token_endpoint", FreeAgentHosts.TokenEndpoint(environment).ToString());
    }

    [Fact]
    public void Host_Throws_ForAnUnrecognisedEnvironment()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeAgentHosts.Host((FreeAgentEnvironment)99));
    }

    [Theory]
    [InlineData(FreeAgentEnvironment.Sandbox, "acmeltd", "https://acmeltd.sandbox.freeagent.com/")]
    [InlineData(FreeAgentEnvironment.Production, "acmeltd", "https://acmeltd.freeagent.com/")]
    public void AppBaseUri_UsesTheAccountSubdomain_NotTheSharedApiHost(
        FreeAgentEnvironment environment, string subdomain, string expected)
    {
        Assert.Equal(expected, FreeAgentHosts.AppBaseUri(environment, subdomain).ToString());
    }

    [Fact]
    public void AppBaseUri_Throws_ForAnUnrecognisedEnvironment()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeAgentHosts.AppBaseUri((FreeAgentEnvironment)99, "acmeltd"));
    }
}
