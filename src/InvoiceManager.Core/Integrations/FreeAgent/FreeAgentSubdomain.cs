using System.Text.RegularExpressions;

namespace InvoiceManager.Core.Integrations.FreeAgent;

/// <summary>
/// A validated FreeAgent account web-app subdomain (the part before ".freeagent.com" or
/// ".sandbox.freeagent.com" when browsing FreeAgent, e.g. "acmeltd" for
/// https://acmeltd.freeagent.com). Composes directly into a hostname (see
/// <c>FreeAgentHosts.AppBaseUri</c>), so this type can only ever hold a single valid DNS label
/// (RFC 1035: 1-63 characters, alphanumeric first/last, hyphens only internally) - an
/// unvalidated string here (from FreeAgent's own API response, or a tampered/corrupted Key
/// Vault secret) could otherwise build a link hosted at an entirely different domain (e.g.
/// "evil.example/path" parses as host "evil.example") or throw while rendering a page.
/// </summary>
public sealed partial class FreeAgentSubdomain : IEquatable<FreeAgentSubdomain>
{
    public string Value { get; }

    private FreeAgentSubdomain(string value) => Value = value;

    /// <summary>
    /// Parses <paramref name="value"/> as a FreeAgent subdomain, or <see cref="None"/> if it
    /// isn't a valid single DNS label - never throws, since both of this type's sources (a
    /// FreeAgent API response, a stored secret) are external data this code doesn't control.
    /// </summary>
    public static Option<FreeAgentSubdomain> TryParse(string? value) =>
        value is not null && Pattern().IsMatch(value)
            ? new FreeAgentSubdomain(value)
            : Option.None;

    public override string ToString() => Value;

    public bool Equals(FreeAgentSubdomain? other) => other is not null && Value == other.Value;

    public override bool Equals(object? obj) => Equals(obj as FreeAgentSubdomain);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(FreeAgentSubdomain? left, FreeAgentSubdomain? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(FreeAgentSubdomain? left, FreeAgentSubdomain? right) => !(left == right);

    // \A/\z (not ^/$) so a trailing newline can't sneak a value like "acmeltd\n" past this -
    // $ matches before a final line terminator, \z only at the true end of the string.
    [GeneratedRegex(@"\A[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\z")]
    private static partial Regex Pattern();
}
