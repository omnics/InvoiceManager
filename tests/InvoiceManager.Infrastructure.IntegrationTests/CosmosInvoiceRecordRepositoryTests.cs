using InvoiceManager.Core;
using InvoiceManager.Infrastructure.CosmosDb;
using Microsoft.Azure.Cosmos;
using NodaMoney;

namespace InvoiceManager.Infrastructure.IntegrationTests;

[Collection("CosmosIntegration")]
[Trait("Category", "Integration")]
public sealed class CosmosInvoiceRecordRepositoryTests : IAsyncLifetime
{
    private const string TestDatabase = "invoicemanager-record-integration-tests";

    private readonly CosmosEmulatorFixture emulator;
    private CosmosInvoiceRecordRepository? repository;

    public CosmosInvoiceRecordRepositoryTests(CosmosEmulatorFixture emulator)
    {
        this.emulator = emulator;
    }

    public async Task InitializeAsync()
    {
        await emulator.EnsureDatabaseAndContainerAsync(
            TestDatabase, new ContainerProperties("invoice-records", "/configurationId"));

        repository = new CosmosInvoiceRecordRepository(emulator.Client, TestDatabase);
    }

    public async Task DisposeAsync()
    {
        await emulator.DeleteDatabaseAsync(TestDatabase);
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_InsertsRecord_WhenNotPresent()
    {
        var record = BuildRecord(
            new InvoiceConfigurationId("create-record"),
            new DateOnly(2025, 7, 1));

        await repository!.CreateIfNotExistsAsync(record);

        var stored = RequireRecord(await repository.GetMostRecentAsync(record.ConfigurationId));
        AssertRecordIdentity(record, stored);
        Assert.True(stored.State is Expected);
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_DoesNotOverwrite_WhenRecordAlreadyExists()
    {
        var configurationId = new InvoiceConfigurationId("idempotent-record");
        var expectedDate = new DateOnly(2025, 7, 1);
        var original = BuildRecord(configurationId, expectedDate, invoiceDescription: "Original");
        var modified = BuildRecord(configurationId, expectedDate, invoiceDescription: "Modified");

        await repository!.CreateIfNotExistsAsync(original);
        await repository.CreateIfNotExistsAsync(modified);

        var stored = RequireRecord(await repository.GetMostRecentAsync(configurationId));
        Assert.Equal("Original", stored.ProcessingSnapshot.InvoiceDescription);
    }

    [Fact]
    public async Task GetMostRecentAsync_ReturnsLatestRecordForConfiguration()
    {
        var configurationId = new InvoiceConfigurationId("most-recent-record");
        var older = BuildRecord(configurationId, new DateOnly(2025, 6, 1));
        var newer = BuildRecord(configurationId, new DateOnly(2025, 7, 1));
        var otherConfiguration = BuildRecord(
            new InvoiceConfigurationId("most-recent-other"),
            new DateOnly(2025, 8, 1));

        await repository!.CreateIfNotExistsAsync(older);
        await repository.CreateIfNotExistsAsync(newer);
        await repository.CreateIfNotExistsAsync(otherConfiguration);

        var stored = RequireRecord(await repository.GetMostRecentAsync(configurationId));
        Assert.Equal(newer.Id, stored.Id);
        Assert.Equal(new DateOnly(2025, 7, 1), stored.ExpectedDate);
    }

    [Fact]
    public async Task GetMostRecentAsync_ReturnsNone_WhenConfigurationHasNoRecords()
    {
        var result = await repository!.GetMostRecentAsync(new InvoiceConfigurationId("missing-records"));

        Assert.True(result is None);
    }

    [Fact]
    public async Task ListDueAsync_ReturnsRetryableRecordsDueOnOrBeforeDate()
    {
        var expectedDue = BuildRecord(
            new InvoiceConfigurationId("due-expected"),
            new DateOnly(2025, 7, 1),
            state: new Expected(Option.None));
        var retrievalErrorDue = BuildRecord(
            new InvoiceConfigurationId("due-retrievalerror"),
            new DateOnly(2025, 7, 3),
            state: new RetrievalError("transient failure"));
        var retrievedDue = BuildRecord(
            new InvoiceConfigurationId("due-retrieved"),
            new DateOnly(2025, 7, 4),
            state: new Retrieved(BuildActualDetails()));
        var futureExpected = BuildRecord(
            new InvoiceConfigurationId("future-expected"),
            new DateOnly(2025, 8, 1),
            state: new Expected(Option.None));
        var notFoundDue = BuildRecord(
            new InvoiceConfigurationId("due-notfound"),
            new DateOnly(2025, 7, 5),
            state: new NotFound("test diagnostic"));
        var savedDue = BuildRecord(
            new InvoiceConfigurationId("saved-due"),
            new DateOnly(2025, 7, 6),
            state: new SavedToOneDrive(
                BuildActualDetails(),
                new OneDriveDetails("/drives/test/root:/Bills/Test/saved.pdf", "test-drive", "saved-item")));
        var freeAgentMatchExpectedDue = BuildRecord(
            new InvoiceConfigurationId("freeagent-match-expected-due"),
            new DateOnly(2025, 7, 7),
            state: new FreeAgentMatchExpected(
                BuildActualDetails(),
                new OneDriveDetails("/drives/test/root:/Bills/Test/fa-match.pdf", "test-drive", "fa-match-item"),
                Option.None));
        var freeAgentErrorDue = BuildRecord(
            new InvoiceConfigurationId("freeagent-error-due"),
            new DateOnly(2025, 7, 8),
            state: new FreeAgentError(
                BuildActualDetails(),
                new OneDriveDetails("/drives/test/root:/Bills/Test/fa-error.pdf", "test-drive", "fa-error-item"),
                "FreeAgent bill locked",
                Option.None));
        var freeAgentInterventionPendingNotDue = BuildRecord(
            new InvoiceConfigurationId("freeagent-intervention-not-due"),
            new DateOnly(2025, 7, 9),
            state: new FreeAgentInterventionPending(
                BuildActualDetails(),
                new OneDriveDetails("/drives/test/root:/Bills/Test/fa-pending.pdf", "test-drive", "fa-pending-item"),
                new FreeAgentInterventionId("freeagent-intervention-1")));

        await repository!.CreateIfNotExistsAsync(expectedDue);
        await repository.CreateIfNotExistsAsync(retrievalErrorDue);
        await repository.CreateIfNotExistsAsync(retrievedDue);
        await repository.CreateIfNotExistsAsync(futureExpected);
        await repository.CreateIfNotExistsAsync(notFoundDue);
        await repository.CreateIfNotExistsAsync(savedDue);
        await repository.CreateIfNotExistsAsync(freeAgentMatchExpectedDue);
        await repository.CreateIfNotExistsAsync(freeAgentErrorDue);
        await repository.CreateIfNotExistsAsync(freeAgentInterventionPendingNotDue);

        var due = await repository.ListDueAsync(new DateOnly(2025, 7, 15));

        // Expected, RetrievalError, Retrieved, FreeAgentMatchExpected, and FreeAgentError
        // are retryable; NotFound (terminal), SavedToOneDrive (done),
        // FreeAgentInterventionPending (decision-gated, not polled), and future-dated
        // records are excluded.
        InvoiceRecordId[] expected =
        [
            expectedDue.Id, retrievalErrorDue.Id, retrievedDue.Id, freeAgentMatchExpectedDue.Id, freeAgentErrorDue.Id,
        ];
        Assert.Equal(
            expected.OrderBy(id => id.Value),
            due.Select(r => r.Id).OrderBy(id => id.Value));
    }

    [Fact]
    public async Task ReplaceAsync_PersistsRetrievalErrorMessage()
    {
        var record = BuildRecord(
            new InvoiceConfigurationId("retrieval-error-record"),
            new DateOnly(2025, 7, 1),
            state: new Expected(Option.None));
        await repository!.CreateIfNotExistsAsync(record);

        var errored = record with { State = new RetrievalError("billing API returned 503") };
        await repository.ReplaceAsync(errored);

        var stored = RequireRecord(await repository.GetMostRecentAsync(record.ConfigurationId));
        if (stored.State is not RetrievalError error)
        {
            Assert.Fail($"Expected RetrievalError but was {stored.State}.");
            return;
        }

        Assert.Equal("billing API returned 503", error.ErrorMessage);
    }

    [Fact]
    public async Task ReplaceAsync_PersistsUpdatedWorkflowState()
    {
        var record = BuildRecord(
            new InvoiceConfigurationId("replace-record"),
            new DateOnly(2025, 7, 1),
            state: new Expected(Option.None));
        await repository!.CreateIfNotExistsAsync(record);

        var actualDetails = BuildActualDetails(
            actualInvoiceDate: new DateOnly(2025, 7, 5),
            sourceInvoiceId: "G152207778");
        var saved = record with
        {
            State = new SavedToOneDrive(
                actualDetails,
                new OneDriveDetails("/drives/test/root:/Bills/Test/replaced.pdf", "test-drive", "replaced-item")),
        };

        await repository.ReplaceAsync(saved);

        var stored = RequireRecord(await repository.GetMostRecentAsync(record.ConfigurationId));
        if (stored.State is not SavedToOneDrive savedState)
        {
            Assert.Fail($"Expected SavedToOneDrive but was {stored.State}.");
            return;
        }

        Assert.Equal(actualDetails, savedState.ActualDetails);
        Assert.Equal("/drives/test/root:/Bills/Test/replaced.pdf", savedState.OneDriveDetails.OneDriveLocation);
    }

    private static InvoiceRecord BuildRecord(
        InvoiceConfigurationId configurationId,
        DateOnly expectedDate,
        string invoiceDescription = "Test Invoice",
        InvoiceWorkflowState? state = null) =>
        new(
            configurationId,
            expectedDate,
            state ?? new Expected(Option.None),
            new InvoiceProcessingSnapshot(
                new MicrosoftBillingIntegrationConfiguration("billing-id"),
                new OneDriveFolder("test-drive", "Test Drive", "test-folder-item", "/Bills/Test"),
                invoiceDescription,
                5,
                new AmountMatchingCriteria(new Money(10.00m, "GBP"), 0.50m),
                VatMode.Exclusive,
                Option.None));

    private static ActualInvoiceDetails BuildActualDetails(
        DateOnly? actualInvoiceDate = null,
        string sourceInvoiceId = "SRC-INVOICE-1") =>
        new(
            actualInvoiceDate ?? new DateOnly(2025, 7, 5),
            new Money(9.99m, "GBP"),
            new SourceInvoiceId(sourceInvoiceId));

    private static InvoiceRecord RequireRecord(Option<InvoiceRecord> result) =>
        result switch
        {
            InvoiceRecord record => record,
            None => throw new InvalidOperationException("Expected an invoice record."),
        };

    private static void AssertRecordIdentity(InvoiceRecord expected, InvoiceRecord actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ConfigurationId, actual.ConfigurationId);
        Assert.Equal(expected.ExpectedDate, actual.ExpectedDate);
        Assert.Equal(expected.ProcessingSnapshot, actual.ProcessingSnapshot);
    }
}
