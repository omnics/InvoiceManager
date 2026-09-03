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
