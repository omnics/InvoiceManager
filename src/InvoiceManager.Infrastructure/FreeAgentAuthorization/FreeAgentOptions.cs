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
}
