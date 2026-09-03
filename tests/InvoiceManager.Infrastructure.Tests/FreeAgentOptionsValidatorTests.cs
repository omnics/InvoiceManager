using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class FreeAgentOptionsValidatorTests
{
    [Fact]
    public void Validate_Succeeds_WhenSubdomainIsEmpty()
    {
        // Empty is the deliberate "not configured" value - it disables the bill link rather
        // than failing startup, since Subdomain is optional.
        var validator = new FreeAgentOptionsValidator();

        var result = validator.Validate(null, new FreeAgentOptions { Subdomain = "" });

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Theory]
    [InlineData("acmeltd")]
    [InlineData("acme-ltd")]
    [InlineData("ACME123")]
    public void Validate_Succeeds_ForABareHostnameLabel(string subdomain)
    {
        var validator = new FreeAgentOptionsValidator();

        var result = validator.Validate(null, new FreeAgentOptions { Subdomain = subdomain });

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Theory]
    [InlineData("acme ltd")] // Whitespace.
    [InlineData("acme/ltd")] // Path separator - could build a link outside the FreeAgent host.
    [InlineData("acme?ltd")] // Query character.
    [InlineData("acme.freeagent.com")] // A full host, not a bare label.
    public void Validate_Fails_WhenSubdomainContainsCharactersUnsafeForAHostname(string subdomain)
    {
        var validator = new FreeAgentOptionsValidator();

        var result = validator.Validate(null, new FreeAgentOptions { Subdomain = subdomain });

        Assert.True(result.Failed);
        Assert.Contains(
            "FreeAgent:Subdomain must contain only letters, digits, and hyphens (it becomes part of a hostname).",
            result.Failures);
    }
}
