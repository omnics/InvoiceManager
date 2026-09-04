using NodaMoney;

namespace InvoiceManager.Core.Integrations.FreeAgent;

/// <summary>
/// The provider-independent criteria used to search for a FreeAgent bill matching
/// a retrieved or reconciled invoice.
/// </summary>
public sealed record FreeAgentBillSearchCriteria(
    FreeAgentContactIdentity ContactUrl,
    string ContactDisplayName,
    DateOnly ExpectedDate,
    int DateToleranceDays,
    Money ExpectedAmount,
    decimal AmountTolerance);

/// <summary>No FreeAgent bill matched the search criteria.</summary>
/// <param name="Diagnostic">
/// Human-readable detail on why nothing matched - the contact, date window,
/// expected amount/tolerance, how many bills FreeAgent returned in the date
/// window, and the nearest rejected candidate's amount, if any. Carried into
/// <see cref="InvoiceManager.Core.FreeAgentMatchExpected"/>'s last-match diagnostic so an
/// administrator can see why a record is stuck without re-querying FreeAgent.
/// </param>
public sealed record NoFreeAgentBillMatch(string Diagnostic);

/// <summary>
/// More than one FreeAgent bill matched the search criteria. Never resolved by
/// silently picking one - the caller must treat this as a conflict.
/// </summary>
public sealed record AmbiguousFreeAgentBillMatch(IReadOnlyList<FreeAgentBillIdentity> Candidates);

/// <summary>
/// Exactly one FreeAgent bill matched the search criteria. Named distinctly from
/// the <c>FreeAgentBillMatched</c> workflow state (which represents the record
/// having reached this point, not the matcher's outcome) to avoid a same-name
/// collision between the two.
/// </summary>
public sealed record FreeAgentBillFound(FreeAgentBillSnapshot Bill);

/// <summary>The outcome of searching for a FreeAgent bill matching an invoice.</summary>
public union FreeAgentBillMatchResult(NoFreeAgentBillMatch, AmbiguousFreeAgentBillMatch, FreeAgentBillFound);

/// <summary>
/// Finds the FreeAgent bill corresponding to a retrieved or reconciled invoice.
/// Pages all relevant bills and matches using the configured date/amount
/// tolerances, never silently choosing among multiple candidates.
/// </summary>
public interface IFreeAgentBillMatcher
{
    Task<FreeAgentBillMatchResult> FindBillAsync(
        FreeAgentBillSearchCriteria criteria, CancellationToken cancellationToken = default);
}
