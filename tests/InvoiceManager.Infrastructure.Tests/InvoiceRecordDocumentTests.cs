using System.Text.Json;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Infrastructure.CosmosDb;
using NodaMoney;

namespace InvoiceManager.Infrastructure.Tests;

public sealed class InvoiceRecordDocumentTests
{
    private const string OneDriveLocation = "/drives/test/root:/Bills/Test/invoice.pdf";
    private const string DriveId = "test-drive";
    private const string ItemId = "invoice-item";

    private static ActualInvoiceDetails SampleActualDetails => new(
        new DateOnly(2025, 7, 5),
        new Money(9.99m, "GBP"),
        new SourceInvoiceId("G152207778"));

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsExpected_WithNoDiagnosticYet()
    {
        var record = BuildRecord(new Expected(Option.None));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsExpected_WithLastDiagnostic()
    {
        var record = BuildRecord(new Expected("2 invoice(s) found but none matched the expected amount."));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsNotFound()
    {
        var record = BuildRecord(new NotFound("No invoice dated within 5 day(s) of 2025-07-01."));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsRetrieved()
    {
        var record = BuildRecord(new Retrieved(SampleActualDetails));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsReconciledFromOneDrive()
    {
        var record = BuildRecord(new ReconciledFromOneDrive(
            SampleActualDetails,
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId),
            "matched by date and amount",
            new DateTimeOffset(2025, 7, 6, 8, 30, 0, TimeSpan.Zero)));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsSavedToOneDrive()
    {
        var record = BuildRecord(new SavedToOneDrive(
            SampleActualDetails,
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId)));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsFreeAgentMatchExpected_WithNoDiagnosticYet()
    {
        var record = BuildRecord(new FreeAgentMatchExpected(
            SampleActualDetails,
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId),
            Option.None));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsFreeAgentMatchExpected_WithLastMatchDiagnostic()
    {
        var record = BuildRecord(new FreeAgentMatchExpected(
            SampleActualDetails,
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId),
            "No FreeAgent bill found in the date window."));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsFreeAgentError_WithNoAttemptedAttachment()
    {
        var record = BuildRecord(new FreeAgentError(
            SampleActualDetails,
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId),
            "FreeAgent bill locked",
            Option.None));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void RoundTrip_PreservesRecord_WhenStateIsFreeAgentError_WithAttemptedAttachment()
    {
        var record = BuildRecord(new FreeAgentError(
            SampleActualDetails,
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId),
            "verification failed on the prior attempt",
            new FreeAgentAttemptedAttachment(
                new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1"),
                new FreeAgentAttachmentMetadata(
                    "2025-07-05 Test Invoice G152207778 £9.99 exc.pdf", 1024, "application/pdf",
                    new DateTimeOffset(2025, 7, 6, 8, 30, 0, TimeSpan.Zero)))));

        var roundTripped = InvoiceRecordDocument.FromRecord(record).ToRecord();

        Assert.Equal(record, roundTripped);
    }

    [Fact]
    public void ToRecord_Throws_WhenPayloadStatusIsMissingActualInvoiceDetails()
    {
        var document = BuildDocument(status: "Retrieved");

        var ex = Assert.Throws<InvalidOperationException>(() => document.ToRecord());
        Assert.Equal(
            "Invoice record document 'config-1_2025-07-01' has status 'Retrieved' " +
            "but is missing 'actualInvoiceDetails'.",
            ex.Message);
    }

    [Fact]
    public void ToRecord_Throws_WhenPayloadStatusIsMissingOneDriveDetails()
    {
        var document = BuildDocument(
            status: "SavedToOneDrive",
            actualDetails: new ActualInvoiceDetailsDocument
            {
                ActualInvoiceDate = "2025-07-05",
                ActualAmount = 9.99m,
                ActualCurrency = "GBP",
                SourceInvoiceId = "G152207778",
            });

        var ex = Assert.Throws<InvalidOperationException>(() => document.ToRecord());
        Assert.Equal(
            "Invoice record document 'config-1_2025-07-01' has status 'SavedToOneDrive' " +
            "but is missing 'oneDriveDetails'.",
            ex.Message);
    }

    [Fact]
    public void ToRecord_DefaultsToNoDiagnostic_WhenNotFoundDocumentPredatesTheField()
    {
        var document = BuildDocument(status: "NotFound");

        var record = document.ToRecord();

        Assert.Equal(new NotFound(Option.None), record.State);
    }

    [Fact]
    public void ToRecord_Throws_WhenStatusIsUnrecognised()
    {
        var document = BuildDocument(status: "Teleported");

        var ex = Assert.Throws<InvalidOperationException>(() => document.ToRecord());
        Assert.Equal(
            "Invoice record document 'config-1_2025-07-01' has unrecognised status 'Teleported'.",
            ex.Message);
    }

    [Fact]
    public void Deserialize_Throws_WhenActualInvoiceDetailsIsMissingItsRequiredProperty()
    {
        const string json = """
            {
              "id": "config-1_2025-07-01",
              "configurationId": "config-1",
              "expectedDate": "2025-07-01",
              "processingSnapshot": {
                "integrationType": "MicrosoftBilling",
                "integrationConfiguration": { "type": "microsoftBilling", "billingAccountId": "billing-id" },
                "oneDriveFolder": { "driveId": "d", "driveName": "Drive", "folderItemId": "f", "folderPath": "/Bills" },
                "invoiceDescription": "Test Invoice",
                "dateToleranceDays": 5,
                "amountMatchingCriteria": {
                  "amount": 10.00,
                  "currency": "GBP",
                  "amountTolerance": 0.50
                },
                "vatMode": "Exclusive"
              },
              "status": "Retrieved",
              "actualInvoiceDetails": {}
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<InvoiceRecordDocument>(json));
    }

    private static InvoiceRecord BuildRecord(InvoiceWorkflowState state) =>
        new(
            new InvoiceConfigurationId("config-1"),
            new DateOnly(2025, 7, 1),
            state,
            BuildSnapshot().ToSnapshot());

    private static InvoiceRecordDocument BuildDocument(
        string status,
        ActualInvoiceDetailsDocument? actualDetails = null,
        OneDriveDetailsDocument? oneDriveDetails = null) =>
        new()
        {
            Id = "config-1_2025-07-01",
            ConfigurationId = "config-1",
            ExpectedDate = "2025-07-01",
            ProcessingSnapshot = BuildSnapshot(),
            Status = status,
            ActualInvoiceDetails = actualDetails,
            OneDriveDetails = oneDriveDetails,
        };

    private static InvoiceProcessingSnapshotDocument BuildSnapshot() => new()
    {
        IntegrationType = "MicrosoftBilling",
        IntegrationConfiguration = new IntegrationConfigurationDocument
        {
            Type = "microsoftBilling",
            BillingAccountId = "billing-id",
        },
        OneDriveFolder = new OneDriveFolderDocument
        {
            DriveId = "d",
            DriveName = "Drive",
            FolderItemId = "f",
            FolderPath = "/Bills",
        },
        InvoiceDescription = "Test Invoice",
        DateToleranceDays = 5,
        AmountMatchingCriteria = new()
        {
            Amount = 10.00m,
            Currency = "GBP",
            AmountTolerance = 0.50m,
        },
        VatMode = "Exclusive",
    };
}
