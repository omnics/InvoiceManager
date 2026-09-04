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
        var subdomain = FreeAgentSubdomain.TryParse("omnicssandbox") is FreeAgentSubdomain value
            ? value
            : throw new InvalidOperationException("Test subdomain did not parse.");

        var url = Bill.WebUrl(FreeAgentEnvironment.Sandbox, subdomain);

        Assert.True(url is Uri found && found.ToString() == "https://omnicssandbox.sandbox.freeagent.com/bills/327959");
    }

    [Fact]
    public void WebUrl_ReturnsNone_WhenNoSubdomainIsKnown_RatherThanGuessingALink()
    {
        Assert.True(Bill.WebUrl(FreeAgentEnvironment.Sandbox, Option.None) is None);
    }
}
