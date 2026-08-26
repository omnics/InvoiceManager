using NodaMoney;

namespace InvoiceManager.Core.Integrations.FreeAgent;

/// <summary>
/// Shared base for InvoiceManager-owned, opaque references to a FreeAgent resource's URL.
/// Stores the resource as a parsed <see cref="Uri"/> rather than a bare string, and
/// centralizes the "absolute https URI" requirement once here rather than duplicating the
/// check in every derived type. Requires an absolute <c>https</c> URI rather than merely
/// <see cref="UriKind.Absolute"/> - on Unix, <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>
/// happily parses a leading-slash path like <c>/v2/bills/1</c> as an absolute <c>file</c>
/// URI, which is never a valid FreeAgent API resource URL.
/// </summary>
public abstract record FreeAgentResourceUrl
{
    public Uri Url { get; }

    private protected FreeAgentResourceUrl(string url)
    {
        Url = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new ArgumentException($"{GetType().Name} must be an absolute https URI.", nameof(url));
    }

    // Accepts an already-parsed Uri (e.g. straight from FreeAgentWireModels' System.Text.Json
    // deserialization) without re-parsing it - OriginalString is the exact text the Uri was
    // built from, so this still runs through the single validation path above rather than
    // duplicating the https/absolute check.
    private protected FreeAgentResourceUrl(Uri url)
        : this(url.OriginalString)
    {
    }
}

/// <summary>
/// An opaque, InvoiceManager-owned reference to a FreeAgent bill's resource URL.
/// Not a Cosmos document ID (no <see cref="IStringId{TSelf}"/>), but still a typed
/// wrapper rather than a bare string, so a bill identity and an item identity can
/// never be swapped at a call site.
/// </summary>
public sealed record FreeAgentBillIdentity : FreeAgentResourceUrl
{
    public FreeAgentBillIdentity(string billUrl) : base(billUrl) { }
    public FreeAgentBillIdentity(Uri billUrl) : base(billUrl) { }
}

/// <summary>An opaque, InvoiceManager-owned reference to a FreeAgent bill item's resource URL.</summary>
public sealed record FreeAgentBillItemIdentity : FreeAgentResourceUrl
{
    public FreeAgentBillItemIdentity(string itemUrl) : base(itemUrl) { }
    public FreeAgentBillItemIdentity(Uri itemUrl) : base(itemUrl) { }
}

/// <summary>
/// An opaque, InvoiceManager-owned reference to a FreeAgent contact's resource URL -
/// used both to search for a contact's bills (<see cref="FreeAgentBillSearchCriteria"/>)
/// and to report which contact a matched bill belongs to. A typed wrapper rather than a
/// bare string so it can never be swapped with a bill or item identity at a call site.
/// </summary>
public sealed record FreeAgentContactIdentity : FreeAgentResourceUrl
{
    public FreeAgentContactIdentity(string contactUrl) : base(contactUrl) { }
    public FreeAgentContactIdentity(Uri contactUrl) : base(contactUrl) { }
}

/// <summary>The status of a FreeAgent bill, enumerated rather than left as FreeAgent's raw string.</summary>
public enum FreeAgentBillStatus
{
    Open,
    Paid,
    PartPaid,
    Overpaid,
    Unrecognised,
}

/// <summary>A single line item on a FreeAgent bill.</summary>
public sealed record FreeAgentBillItem(FreeAgentBillItemIdentity ItemUrl, string Description, Money TotalValue);

/// <summary>
/// Metadata about a bill's attachment, retained for audit/retry diagnosis. Never
/// carries the attachment's bytes/base64 content.
/// </summary>
public sealed record FreeAgentAttachmentMetadata(
    string FileName,
    long FileSizeBytes,
    string ContentType,
    DateTimeOffset UploadedAt);

/// <summary>
/// FreeAgent's accepted MIME type for a PDF bill attachment. Not the standard
/// "application/pdf" - FreeAgent's documented <c>attachment.content_type</c> values are
/// image/png, image/x-png, image/jpeg, image/jpg, image/gif, and application/x-pdf; the
/// standard MIME type is rejected with 400 Bad Request. The single source of truth for
/// every place that sends or compares a FreeAgent attachment's content type.
/// </summary>
public static class FreeAgentAttachmentContentType
{
    public const string Pdf = "application/x-pdf";

    /// <summary>
    /// True for any content type FreeAgent may legitimately report for a PDF attachment on
    /// read. Confirmed against the sandbox (undocumented): FreeAgent's write and read
    /// vocabularies for this field disagree - an upload must use <see cref="Pdf"/>
    /// ("application/x-pdf") or it is rejected with 400, but reading that same attachment back
    /// reports the standard "application/pdf" instead. Comparisons against a value FreeAgent
    /// itself returned (an existing or just-uploaded attachment) must accept both, or a
    /// provider-side normalization that isn't actually wrong looks like a failure.
    /// </summary>
    public static bool IsPdf(string? contentType) =>
        string.Equals(contentType, Pdf, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Core-owned, provider-agnostic verified state of a FreeAgent bill after a
/// query or mutation. The integration project must repopulate every field after
/// every relevant mutation - this is what "verify status, total, paid amount,
/// amount due, paid date, bill items, contact, and attachment metadata after
/// every relevant mutation" means at the Core boundary.
/// </summary>
public sealed record FreeAgentBillSnapshot(
    FreeAgentBillIdentity Identity,
    FreeAgentBillStatus Status,
    DateOnly DatedOn,
    DateOnly DueOn,
    Money TotalValue,
    Money PaidValue,
    Money DueValue,
    Option<DateOnly> PaidOn,
    FreeAgentContactIdentity ContactUrl,
    string Reference,
    IReadOnlyList<FreeAgentBillItem> Items,
    Option<FreeAgentAttachmentMetadata> Attachment);
