using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class FreeAgentOptionsValidatorTests
{
    private const string ExpectedFailureMessage =
        "FreeAgent:Subdomain must be a valid DNS label: 1-63 characters, letters/digits/hyphens only, " +
        "and must not start or end with a hyphen.";

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
    [InlineData("a")] // Single character - the shortest valid label.
    public void Validate_Succeeds_ForAValidDnsLabel(string subdomain)
    {
        var validator = new FreeAgentOptionsValidator();

        var result = validator.Validate(null, new FreeAgentOptions { Subdomain = subdomain });

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Fact]
    public void Validate_Succeeds_AtTheMaximumDnsLabelLength()
    {
        var validator = new FreeAgentOptionsValidator();
        var subdomain = new string('a', 63);

        var result = validator.Validate(null, new FreeAgentOptions { Subdomain = subdomain });

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Theory]
    [InlineData("acme ltd")] // Whitespace.
    [InlineData("acme/ltd")] // Path separator - could build a link outside the FreeAgent host.
    [InlineData("acme?ltd")] // Query character.
    [InlineData("acme.freeagent.com")] // A full host, not a bare label.
    [InlineData("-")] // Bare hyphen.
    [InlineData("-acme")] // Leading hyphen.
    [InlineData("acme-")] // Trailing hyphen.
    [InlineData("acmeltd\n")] // Trailing newline - $ matches before it, \z (used here) does not.
    [InlineData("acmeltd\r\n")]
    public void Validate_Fails_WhenSubdomainIsNotAValidDnsLabel(string subdomain)
    {
        var validator = new FreeAgentOptionsValidator();

        var result = validator.Validate(null, new FreeAgentOptions { Subdomain = subdomain });

        Assert.True(result.Failed);
        Assert.Contains(ExpectedFailureMessage, result.Failures);
    }

    [Fact]
    public void Validate_Fails_WhenSubdomainExceedsTheMaximumDnsLabelLength()
    {
        var validator = new FreeAgentOptionsValidator();
        var subdomain = new string('a', 64);

        var result = validator.Validate(null, new FreeAgentOptions { Subdomain = subdomain });

        Assert.True(result.Failed);
        Assert.Contains(ExpectedFailureMessage, result.Failures);
    }
}
