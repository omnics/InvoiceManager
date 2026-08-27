using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using NodaMoney;

namespace InvoiceManager.Infrastructure.CosmosDb;

/// <summary>
/// The Cosmos JSON shape for <see cref="ActualInvoiceDetails"/>. The sub-object
/// is present only when the record's state carries actual invoice values; when
/// present, every property is required.
/// </summary>
internal sealed class ActualInvoiceDetailsDocument
{
    [JsonPropertyName("actualInvoiceDate")]
    public required string ActualInvoiceDate { get; init; }

    [JsonPropertyName("actualAmount")]
    public required decimal ActualAmount { get; init; }

    [JsonPropertyName("actualCurrency")]
    public required string ActualCurrency { get; init; }

    [JsonPropertyName("sourceInvoiceId")]
    public required string SourceInvoiceId { get; init; }

    public ActualInvoiceDetails ToDetails() =>
        new(
            DateOnly.ParseExact(ActualInvoiceDate, "O", CultureInfo.InvariantCulture),
            new Money(ActualAmount, ActualCurrency),
            new Core.SourceInvoiceId(SourceInvoiceId));

    public static ActualInvoiceDetailsDocument FromDetails(ActualInvoiceDetails details) =>
        new()
        {
            ActualInvoiceDate = details.ActualInvoiceDate.ToString("O", CultureInfo.InvariantCulture),
            ActualAmount = details.ActualAmount.Amount,
            ActualCurrency = details.ActualAmount.Currency.Code,
            SourceInvoiceId = details.SourceInvoiceId.Value,
        };
}

/// <summary>
/// The Cosmos JSON shape for <see cref="OneDriveDetails"/>. The sub-object is
/// present only when the record's state carries a OneDrive location; when
/// present, every property is required.
/// </summary>
internal sealed class OneDriveDetailsDocument
{
    [JsonPropertyName("oneDriveLocation")]
    public required string OneDriveLocation { get; init; }

    [JsonPropertyName("driveId")]
    public required string DriveId { get; init; }

    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    public OneDriveDetails ToDetails() => new(OneDriveLocation, DriveId, ItemId);

    public static OneDriveDetailsDocument FromDetails(OneDriveDetails details) =>
        new() { OneDriveLocation = details.OneDriveLocation, DriveId = details.DriveId, ItemId = details.ItemId };
}

/// <summary>The Cosmos JSON shape for <see cref="FreeAgentAttachmentMetadata"/>.</summary>
internal sealed class FreeAgentAttachmentMetadataDocument
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("fileSizeBytes")]
    public required long FileSizeBytes { get; init; }

    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("uploadedAt")]
    public required string UploadedAt { get; init; }

    public FreeAgentAttachmentMetadata ToMetadata() =>
        new(
            FileName,
            FileSizeBytes,
            ContentType,
            DateTimeOffset.ParseExact(UploadedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public static FreeAgentAttachmentMetadataDocument FromMetadata(FreeAgentAttachmentMetadata metadata) => new()
    {
        FileName = metadata.FileName,
        FileSizeBytes = metadata.FileSizeBytes,
        ContentType = metadata.ContentType,
        UploadedAt = metadata.UploadedAt.ToString("O", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The Cosmos DB document shape for an invoice record.
/// Maps between the Cosmos JSON structure and <see cref="InvoiceRecord"/>.
/// The <c>status</c> string discriminates the workflow state; the nested
/// detail sub-objects are present exactly when the state requires them.
/// </summary>
internal sealed class InvoiceRecordDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("configurationId")]
    public required string ConfigurationId { get; init; }

    [JsonPropertyName("expectedDate")]
    public required string ExpectedDate { get; init; }

    [JsonPropertyName("processingSnapshot")]
    public required InvoiceProcessingSnapshotDocument ProcessingSnapshot { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("actualInvoiceDetails")]
    public ActualInvoiceDetailsDocument? ActualInvoiceDetails { get; init; }

    [JsonPropertyName("oneDriveDetails")]
    public OneDriveDetailsDocument? OneDriveDetails { get; init; }

    // Present only for the RetrievalError state: the technical failure detail.
    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }

    // Present for Expected, NotFound, and FreeAgentMatchExpected: why the most
    // recent match attempt found nothing (or was ambiguous). Absent on Expected
    // and FreeAgentMatchExpected when no attempt has happened yet, and on
    // documents written before this field existed - both read back as "no
    // diagnostic yet" rather than an error.
    [JsonPropertyName("lastMatchDiagnostic")]
    public string? LastMatchDiagnostic { get; init; }

    // Present only for the ReconciledFromOneDrive state: why the existing file was
    // accepted and when reconciliation occurred (ISO 8601 round-trip).
    [JsonPropertyName("matchReason")]
    public string? MatchReason { get; init; }

    [JsonPropertyName("reconciledAt")]
    public string? ReconciledAt { get; init; }

    // Present for every FreeAgent* state: the matched bill's resource URL, the
    // idempotency anchor a later run resumes reconciliation from instead of
    // re-searching.
    [JsonPropertyName("freeAgentBillUrl")]
    public string? FreeAgentBillUrl { get; init; }

    // Present only for FreeAgentAttached: the verified attachment metadata, used to
    // decide whether a later retry's upload is already correct.
    [JsonPropertyName("freeAgentAttachment")]
    public FreeAgentAttachmentMetadataDocument? FreeAgentAttachment { get; init; }

    // Present only for FreeAgentInterventionPending: the pending intervention this
    // record is waiting on a decision for.
    [JsonPropertyName("freeAgentInterventionId")]
    public string? FreeAgentInterventionId { get; init; }

    public InvoiceRecord ToRecord() =>
        new(
            new InvoiceConfigurationId(ConfigurationId),
            DateOnly.ParseExact(ExpectedDate, "O", CultureInfo.InvariantCulture),
            ToState(),
            ProcessingSnapshot.ToSnapshot());

    public static InvoiceRecordDocument FromRecord(InvoiceRecord record)
    {
        var fields = StorageFields(record.State);
        return new InvoiceRecordDocument
        {
            Id = record.Id.Value,
            ConfigurationId = record.ConfigurationId.Value,
            ExpectedDate = record.ExpectedDate.ToString("O", CultureInfo.InvariantCulture),
            ProcessingSnapshot = InvoiceProcessingSnapshotDocument.FromSnapshot(record.ProcessingSnapshot),
            Status = fields.Status,
            ActualInvoiceDetails = fields.ActualDetails,
            OneDriveDetails = fields.OneDriveDetails,
            LastError = fields.LastError,
            LastMatchDiagnostic = fields.LastMatchDiagnostic,
            MatchReason = fields.MatchReason,
            ReconciledAt = fields.ReconciledAt,
            FreeAgentBillUrl = fields.FreeAgentBillUrl,
            FreeAgentAttachment = fields.FreeAgentAttachment,
            FreeAgentInterventionId = fields.FreeAgentInterventionId,
        };
    }

    private InvoiceWorkflowState ToState() => Status switch
    {
        nameof(Expected) => new Expected(LastMatchDiagnostic is { } expectedDiagnostic ? expectedDiagnostic : Option.None),
        nameof(NotFound) => new NotFound(LastMatchDiagnostic is { } notFoundDiagnostic ? notFoundDiagnostic : Option.None),
        nameof(RetrievalError) => new RetrievalError(LastError ?? string.Empty),
        nameof(Retrieved) => new Retrieved(RequiredActualDetails()),
        nameof(ReconciledFromOneDrive) => new ReconciledFromOneDrive(
            RequiredActualDetails(),
            RequiredOneDriveDetails(),
            RequiredMatchReason(),
            RequiredReconciledAt()),
        nameof(SavedToOneDrive) => new SavedToOneDrive(RequiredActualDetails(), RequiredOneDriveDetails()),
        nameof(FreeAgentMatchExpected) => new FreeAgentMatchExpected(
            RequiredActualDetails(),
            RequiredOneDriveDetails(),
            LastMatchDiagnostic is { } matchExpectedDiagnostic ? matchExpectedDiagnostic : Option.None),
        nameof(FreeAgentBillMatched) => new FreeAgentBillMatched(
            RequiredActualDetails(), RequiredOneDriveDetails(), RequiredFreeAgentBillIdentity()),
        nameof(FreeAgentBillReconciled) => new FreeAgentBillReconciled(
            RequiredActualDetails(), RequiredOneDriveDetails(), RequiredFreeAgentBillIdentity()),
        nameof(FreeAgentAttached) => new FreeAgentAttached(
            RequiredActualDetails(), RequiredOneDriveDetails(), RequiredFreeAgentBillIdentity(), RequiredFreeAgentAttachment()),
        nameof(FreeAgentInterventionPending) => new FreeAgentInterventionPending(
            RequiredActualDetails(), RequiredOneDriveDetails(), RequiredFreeAgentInterventionId()),
        nameof(FreeAgentError) => new FreeAgentError(
            RequiredActualDetails(), RequiredOneDriveDetails(), LastError ?? string.Empty,
            FreeAgentBillUrl is { } attemptedBillUrl && FreeAgentAttachment is { } attemptedAttachment
                ? new FreeAgentAttemptedAttachment(new FreeAgentBillIdentity(attemptedBillUrl), attemptedAttachment.ToMetadata())
                : Option.None),
        _ => throw new InvalidOperationException(
            $"Invoice record document '{Id}' has unrecognised status '{Status}'."),
    };

    private ActualInvoiceDetails RequiredActualDetails() =>
        ActualInvoiceDetails?.ToDetails()
        ?? throw new InvalidOperationException(
            $"Invoice record document '{Id}' has status '{Status}' but is missing 'actualInvoiceDetails'.");

    private OneDriveDetails RequiredOneDriveDetails() =>
        OneDriveDetails?.ToDetails()
        ?? throw new InvalidOperationException(
            $"Invoice record document '{Id}' has status '{Status}' but is missing 'oneDriveDetails'.");

    private FreeAgentBillIdentity RequiredFreeAgentBillIdentity() =>
        FreeAgentBillUrl is { } url
            ? new FreeAgentBillIdentity(url)
            : throw new InvalidOperationException(
                $"Invoice record document '{Id}' has status '{Status}' but is missing 'freeAgentBillUrl'.");

    private FreeAgentAttachmentMetadata RequiredFreeAgentAttachment() =>
        FreeAgentAttachment?.ToMetadata()
        ?? throw new InvalidOperationException(
            $"Invoice record document '{Id}' has status '{Status}' but is missing 'freeAgentAttachment'.");

    private Core.FreeAgentInterventionId RequiredFreeAgentInterventionId() =>
        FreeAgentInterventionId is { } id
            ? new Core.FreeAgentInterventionId(id)
            : throw new InvalidOperationException(
                $"Invoice record document '{Id}' has status '{Status}' but is missing 'freeAgentInterventionId'.");

    private string RequiredMatchReason() =>
        MatchReason
        ?? throw new InvalidOperationException(
            $"Invoice record document '{Id}' has status '{Status}' but is missing 'matchReason'.");

    private DateTimeOffset RequiredReconciledAt() =>
        ReconciledAt is { } value
            ? DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : throw new InvalidOperationException(
                $"Invoice record document '{Id}' has status '{Status}' but is missing 'reconciledAt'.");

    private static StorageFieldSet StorageFields(InvoiceWorkflowState state) => state switch
    {
        Expected expected => new()
        {
            Status = nameof(Expected),
            LastMatchDiagnostic = expected.LastDiagnostic switch { string d => d, None => null },
        },
        NotFound notFound => new()
        {
            Status = nameof(NotFound),
            LastMatchDiagnostic = notFound.Diagnostic switch { string d => d, None => null },
        },
        RetrievalError error => new() { Status = nameof(RetrievalError), LastError = error.ErrorMessage },
        Retrieved retrieved => new()
        {
            Status = nameof(Retrieved),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(retrieved.ActualDetails),
        },
        ReconciledFromOneDrive reconciled => new()
        {
            Status = nameof(ReconciledFromOneDrive),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(reconciled.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(reconciled.OneDriveDetails),
            MatchReason = reconciled.MatchReason,
            ReconciledAt = reconciled.ReconciledAt.ToString("O", CultureInfo.InvariantCulture),
        },
        SavedToOneDrive saved => new()
        {
            Status = nameof(SavedToOneDrive),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(saved.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(saved.OneDriveDetails),
        },
        FreeAgentMatchExpected matchExpected => new()
        {
            Status = nameof(FreeAgentMatchExpected),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(matchExpected.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(matchExpected.OneDriveDetails),
            LastMatchDiagnostic = matchExpected.LastMatchDiagnostic switch { string d => d, None => null },
        },
        FreeAgentBillMatched matched => new()
        {
            Status = nameof(FreeAgentBillMatched),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(matched.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(matched.OneDriveDetails),
            FreeAgentBillUrl = matched.Bill.Url.OriginalString,
        },
        FreeAgentBillReconciled reconciledBill => new()
        {
            Status = nameof(FreeAgentBillReconciled),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(reconciledBill.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(reconciledBill.OneDriveDetails),
            FreeAgentBillUrl = reconciledBill.Bill.Url.OriginalString,
        },
        FreeAgentAttached attached => new()
        {
            Status = nameof(FreeAgentAttached),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(attached.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(attached.OneDriveDetails),
            FreeAgentBillUrl = attached.Bill.Url.OriginalString,
            FreeAgentAttachment = FreeAgentAttachmentMetadataDocument.FromMetadata(attached.Attachment),
        },
        FreeAgentInterventionPending pending => new()
        {
            Status = nameof(FreeAgentInterventionPending),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(pending.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(pending.OneDriveDetails),
            FreeAgentInterventionId = pending.InterventionId.Value,
        },
        FreeAgentError freeAgentError => new()
        {
            Status = nameof(FreeAgentError),
            ActualDetails = ActualInvoiceDetailsDocument.FromDetails(freeAgentError.ActualDetails),
            OneDriveDetails = OneDriveDetailsDocument.FromDetails(freeAgentError.OneDriveDetails),
            LastError = freeAgentError.ErrorMessage,
            FreeAgentBillUrl = freeAgentError.AttemptedAttachment is FreeAgentAttemptedAttachment attempted
                ? attempted.Bill.Url.OriginalString
                : null,
            FreeAgentAttachment = freeAgentError.AttemptedAttachment is FreeAgentAttemptedAttachment attempted2
                ? FreeAgentAttachmentMetadataDocument.FromMetadata(attempted2.Attachment)
                : null,
        },
    };

    private sealed record StorageFieldSet
    {
        public required string Status { get; init; }
        public ActualInvoiceDetailsDocument? ActualDetails { get; init; }
        public OneDriveDetailsDocument? OneDriveDetails { get; init; }
        public string? LastError { get; init; }
        public string? LastMatchDiagnostic { get; init; }
        public string? MatchReason { get; init; }
        public string? ReconciledAt { get; init; }
        public string? FreeAgentBillUrl { get; init; }
        public FreeAgentAttachmentMetadataDocument? FreeAgentAttachment { get; init; }
        public string? FreeAgentInterventionId { get; init; }
    }
}

internal sealed class InvoiceProcessingSnapshotDocument
{
    /// <summary>
    /// Retained for Cosmos query/index filtering. Written from the snapshot's
    /// (derived) <see cref="Core.IntegrationType"/> on save, but not read back on
    /// load — the integration type is instead derived from <see cref="IntegrationConfiguration"/>.
    /// </summary>
    [JsonPropertyName("integrationType")]
    public required string IntegrationType { get; init; }

    [JsonPropertyName("integrationConfiguration")]
    public required IntegrationConfigurationDocument IntegrationConfiguration { get; init; }

    [JsonPropertyName("oneDriveFolder")]
    public required OneDriveFolderDocument OneDriveFolder { get; init; }

    [JsonPropertyName("invoiceDescription")]
    public required string InvoiceDescription { get; init; }

    [JsonPropertyName("dateToleranceDays")]
    public required int DateToleranceDays { get; init; }

    [JsonPropertyName("amountMatchingCriteria")]
    public AmountMatchingCriteriaDocument? AmountMatchingCriteria { get; init; }

    [JsonPropertyName("vatMode")]
    public required string VatMode { get; init; }

    [JsonPropertyName("freeAgentMatching")]
    public FreeAgentBillMatchingDocument? FreeAgentMatching { get; init; }

    public InvoiceProcessingSnapshot ToSnapshot() => new(
        IntegrationConfiguration.ToConfiguration(),
        OneDriveFolder.ToFolder(),
        InvoiceDescription,
        DateToleranceDays,
        AmountMatchingCriteria is { } criteria ? criteria.ToCriteria() : Option.None,
        Enum.Parse<VatMode>(VatMode, true),
        FreeAgentMatching is { } matching ? matching.ToMatching() : Option.None);

    public static InvoiceProcessingSnapshotDocument FromSnapshot(InvoiceProcessingSnapshot snapshot) => new()
    {
        IntegrationType = snapshot.IntegrationType.ToString(),
        IntegrationConfiguration = IntegrationConfigurationDocument.FromConfiguration(snapshot.IntegrationConfiguration),
        OneDriveFolder = OneDriveFolderDocument.FromFolder(snapshot.OneDriveFolder),
        InvoiceDescription = snapshot.InvoiceDescription,
        DateToleranceDays = snapshot.DateToleranceDays,
        AmountMatchingCriteria = snapshot.AmountMatchingCriteria switch
        {
            AmountMatchingCriteria criteria => AmountMatchingCriteriaDocument.FromCriteria(criteria),
            None => null,
        },
        VatMode = snapshot.VatMode.ToString(),
        FreeAgentMatching = snapshot.FreeAgentMatching switch
        {
            FreeAgentBillMatching matching => FreeAgentBillMatchingDocument.FromMatching(matching),
            None => null,
        },
    };
}
