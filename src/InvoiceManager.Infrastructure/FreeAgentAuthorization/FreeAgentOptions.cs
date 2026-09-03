using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

/// <summary>
/// Which FreeAgent host this deployment talks to. Deliberately a closed enum, not a free-text
/// API base URL - there are only ever two valid FreeAgent hosts (see
/// <see cref="FreeAgentHosts"/>), so an "unrecognised host" cannot even be configured.
/// </summary>
public enum FreeAgentEnvironment
{
    Sandbox,
    Production
}

public sealed class FreeAgentOptions
{
    public const string SectionName = "FreeAgent";

    public FreeAgentEnvironment Environment { get; set; }

    /// <summary>
    /// This FreeAgent account's web-app subdomain (the part before ".freeagent.com" or
    /// ".sandbox.freeagent.com" when browsing FreeAgent, e.g. "acmeltd" for
    /// https://acmeltd.freeagent.com) - distinct from <see cref="FreeAgentHosts"/>'s API
    /// hosts, which are shared across every FreeAgent account. Unset (empty) disables
    /// building a browsable bill link rather than guessing at one.
    /// </summary>
    public string Subdomain { get; set; } = "";
}

/// <summary>
/// Validates <see cref="FreeAgentOptions.Subdomain"/>'s shape when set. It composes directly
/// into a hostname (see <see cref="FreeAgentHosts.AppBaseUri"/>) - an unvalidated value
/// containing whitespace, a slash, or a query character could throw when rendered, or build a
/// link to a host outside FreeAgent entirely.
/// </summary>
public sealed partial class FreeAgentOptionsValidator : IValidateOptions<FreeAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, FreeAgentOptions options)
    {
        if (string.IsNullOrEmpty(options.Subdomain))
        {
            return ValidateOptionsResult.Success;
        }

        return SubdomainPattern().IsMatch(options.Subdomain)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "FreeAgent:Subdomain must be a valid DNS label: 1-63 characters, letters/digits/hyphens only, " +
                "and must not start or end with a hyphen.");
    }

    // A single DNS label (RFC 1035): 1-63 characters, alphanumeric first/last, hyphens only
    // internally - anything looser (a bare "-", a leading/trailing hyphen, over 63 characters)
    // is not a real hostname component, even though Uri itself would accept some of these.
    // \A/\z (not ^/$) so a trailing newline can't sneak a value like "acmeltd\n" past this -
    // $ matches before a final line terminator, \z only at the true end of the string.
    [GeneratedRegex(@"\A[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\z")]
    private static partial Regex SubdomainPattern();
}
