namespace InvoiceManager.Core.Integrations;

/// <summary>
/// No source-system invoice satisfied the supplied criteria.
/// </summary>
/// <param name="Diagnostic">
/// Human-readable detail on why nothing matched - the search window, expected
/// amount/tolerance, how many candidates were considered, and the nearest
/// rejected candidate's actual date/amount, if any. Carried into the
/// <see cref="InvoiceManager.Core.Expected"/>/<see cref="InvoiceManager.Core.NotFound"/> workflow states so an
/// administrator can see why a record hasn't matched without re-deriving it
/// from provider logs.
/// </param>
public sealed record NoInvoiceMatch(string Diagnostic);

/// <summary>
/// A source-system invoice satisfied the criteria. Carries the downloaded PDF
/// bytes (the integration extracts a single PDF from any ZIP itself) together
/// with the actual values read from the invoice.
/// </summary>
public sealed record InvoiceMatch(byte[] PdfContent, ActualInvoiceDetails Details);

/// <summary>The outcome of asking a source integration to find an invoice.</summary>
public union InvoiceSourceResult(NoInvoiceMatch, InvoiceMatch);
