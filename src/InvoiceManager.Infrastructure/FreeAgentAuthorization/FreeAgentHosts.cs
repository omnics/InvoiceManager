namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

/// <summary>
/// The fixed FreeAgent hosts and derived endpoints for each <see cref="FreeAgentEnvironment"/>.
/// Every URL FreeAgent code needs is computed from these, rather than read from separate
/// configuration - production code can never be pointed at an unrecognised host.
/// </summary>
public static class FreeAgentHosts
{
    public const string SandboxHost = "api.sandbox.freeagent.com";
    public const string ProductionHost = "api.freeagent.com";

    public static string Host(FreeAgentEnvironment environment) => environment switch
    {
        FreeAgentEnvironment.Sandbox => SandboxHost,
        FreeAgentEnvironment.Production => ProductionHost,
        _ => throw new ArgumentOutOfRangeException(
            nameof(environment), environment, "Unrecognised FreeAgent environment.")
    };

    public static Uri ApiBaseUri(FreeAgentEnvironment environment) =>
        new($"https://{Host(environment)}/v2/");

    public static Uri AuthorizationEndpoint(FreeAgentEnvironment environment) =>
        new($"https://{Host(environment)}/v2/approve_app");

    public static Uri TokenEndpoint(FreeAgentEnvironment environment) =>
        new($"https://{Host(environment)}/v2/token_endpoint");

    /// <summary>
    /// The base of this account's browsable FreeAgent web app (not the API host above) -
    /// e.g. https://acmeltd.freeagent.com/ or https://acmeltd.sandbox.freeagent.com/ - for
    /// building links an operator can click to view a resource in FreeAgent's own UI.
    /// </summary>
    public static Uri AppBaseUri(FreeAgentEnvironment environment, string subdomain) => environment switch
    {
        FreeAgentEnvironment.Sandbox => new Uri($"https://{subdomain}.sandbox.freeagent.com/"),
        FreeAgentEnvironment.Production => new Uri($"https://{subdomain}.freeagent.com/"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(environment), environment, "Unrecognised FreeAgent environment."),
    };
}
