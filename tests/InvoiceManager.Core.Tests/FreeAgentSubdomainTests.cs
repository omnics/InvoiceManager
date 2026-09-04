using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Core.Tests;

public sealed class FreeAgentSubdomainTests
{
    [Theory]
    [InlineData("acmeltd")]
    [InlineData("acme-ltd")]
    [InlineData("ACME123")]
    [InlineData("a")] // Single character - the shortest valid label.
    public void TryParse_Succeeds_ForAValidDnsLabel(string value)
    {
        Assert.True(FreeAgentSubdomain.TryParse(value) is FreeAgentSubdomain subdomain && subdomain.Value == value);
    }

    [Fact]
    public void TryParse_Succeeds_AtTheMaximumDnsLabelLength()
    {
        var value = new string('a', 63);

        Assert.True(FreeAgentSubdomain.TryParse(value) is FreeAgentSubdomain subdomain && subdomain.Value == value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme ltd")] // Whitespace.
    [InlineData("acme/ltd")] // Path separator - could build a link outside the FreeAgent host.
    [InlineData("acme?ltd")] // Query character.
    [InlineData("acme.freeagent.com")] // A full host, not a bare label.
    [InlineData("evil.example/path")] // Would parse as host "evil.example" once composed into a URL.
    [InlineData("-")] // Bare hyphen.
    [InlineData("-acme")] // Leading hyphen.
    [InlineData("acme-")] // Trailing hyphen.
    [InlineData("acmeltd\n")] // Trailing newline - $ matches before it, \z (used here) does not.
    [InlineData("acmeltd\r\n")]
    public void TryParse_ReturnsNone_WhenNotAValidDnsLabel(string? value)
    {
        Assert.True(FreeAgentSubdomain.TryParse(value) is None);
    }

    [Fact]
    public void TryParse_ReturnsNone_WhenExceedingTheMaximumDnsLabelLength()
    {
        var value = new string('a', 64);

        Assert.True(FreeAgentSubdomain.TryParse(value) is None);
    }
}
