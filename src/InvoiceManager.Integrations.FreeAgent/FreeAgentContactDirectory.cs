using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Integrations.FreeAgent;

/// <summary>
/// Implements contact search/lookup against FreeAgent's contacts endpoint.
/// FreeAgent has no free-text search parameter on <c>GET /v2/contacts</c> (only
/// view/sort/updated_since filters), so <see cref="SearchAsync"/> pages through
/// contacts and filters client-side - the same "server does what it can, filter
/// the rest" split used for bill matching's amount-tolerance filtering.
/// </summary>
internal sealed class FreeAgentContactDirectory(FreeAgentApiClient client) : IFreeAgentContactDirectory
{
    private const int PageSize = 100;

    // FreeAgent has no server-side text filter, so every page is the same fixed,
    // alphabetically-sorted slice of contacts regardless of query - narrowing the query only
    // changes how many of an already-fetched page's contacts match, never which pages get
    // fetched. A low cap here would therefore make contacts past it permanently unreachable no
    // matter what the administrator types, not just "findable with a more specific search" - so
    // this is a generous safety bound against a runaway loop on a pathologically large or
    // misbehaving contact list, not a usability tradeoff.
    private const int MaxPagesScanned = 50;
    private const int MaxResults = 20;

    public async Task<IReadOnlyList<FreeAgentContact>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        var results = new List<FreeAgentContact>();
        var page = 1;
        while (results.Count < MaxResults && page <= MaxPagesScanned)
        {
            var pageResults = await client.SearchContactsPageAsync(page, PageSize, cancellationToken);
            if (pageResults.Count == 0)
                break;

            foreach (var wire in pageResults)
            {
                if (Matches(wire, query))
                {
                    results.Add(wire.ToContact());
                    if (results.Count == MaxResults)
                        break;
                }
            }

            if (pageResults.Count < PageSize)
                break;

            page++;
        }

        return results;
    }

    public async Task<Option<FreeAgentContact>> GetAsync(
        FreeAgentContactIdentity url, CancellationToken cancellationToken = default)
    {
        // None means specifically "FreeAgent has no contact at this URL" (GetContactAsync
        // translates a 404 into null at its own boundary) - a genuine, expected functional
        // outcome the caller can act on directly. Anything else (a host-mismatch guard, a non-404
        // error status, a network failure) is a system error, not "not found", and must not be
        // collapsed into the same None outcome - that would hide a transient FreeAgent outage or
        // a misconfigured environment behind a message telling the administrator their contact
        // was deleted. It's the caller's job (see ConfigurationFormPageModel.RefreshFreeAgentContactAsync)
        // to catch and translate this into a form error with diagnostic detail, the same way
        // OnGetFreeAgentContactsAsync already does for the search path.
        var wire = await client.GetContactAsync(url.Url.OriginalString, cancellationToken);
        return wire is not null ? wire.ToContact() : Option.None;
    }

    private static bool Matches(ContactWire wire, string query) =>
        Contains(wire.OrganisationName, query) ||
        Contains(wire.FirstName, query) ||
        Contains(wire.LastName, query) ||
        Contains($"{wire.FirstName} {wire.LastName}".Trim(), query);

    private static bool Contains(string? value, string query) =>
        value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
