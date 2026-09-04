namespace InvoiceManager.Core.Integrations.FreeAgent;

/// <summary>
/// The FreeAgent company (account) a set of OAuth credentials are authorized against.
/// Fetched from FreeAgent's own <c>GET /v2/company</c> resource - only <see cref="Subdomain"/>
/// is needed today, but modelled as its own type (rather than returning a bare string) so more
/// of that resource's fields can be added later without another signature change.
/// </summary>
public sealed record FreeAgentCompany(FreeAgentSubdomain Subdomain);
