using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class FreeAgentBillWebLinkExtensionsTests
{
    private static readonly FreeAgentBillIdentity Bill =
        new("https://api.sandbox.freeagent.com/v2/bills/327959");

    [Fact]
    public void WebUrl_BuildsTheBrowsableLink_FromTheAccountSubdomainAndTheBillsId()
    {
        var options = new FreeAgentOptions { Environment = FreeAgentEnvironment.Sandbox, Subdomain = "omnicssandbox" };

        var url = Bill.WebUrl(options);

        Assert.True(url is Uri found && found.ToString() == "https://omnicssandbox.sandbox.freeagent.com/bills/327959");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WebUrl_ReturnsNone_WhenNoSubdomainIsConfigured_RatherThanGuessingALink(string? subdomain)
    {
        var options = new FreeAgentOptions { Environment = FreeAgentEnvironment.Sandbox, Subdomain = subdomain! };

        Assert.True(Bill.WebUrl(options) is None);
    }
}
