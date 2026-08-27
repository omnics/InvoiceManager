using System.Globalization;
using InvoiceManager.Core.Integrations;
using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.Core.Repositories;
using InvoiceManager.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaMoney;

namespace InvoiceManager.Core.Tests;

public sealed class DueInvoiceProcessorTests
{
    private static readonly DateOnly Today = new(2025, 7, 15);

    [Fact]
    public async Task ProcessDueAsync_UsesRecordSnapshot_WhenLiveConfigurationWasEdited()
    {
        var original = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var record = Records.Build(original, expectedDate: new DateOnly(2025, 7, 10));
        var edited = original with
        {
            InvoiceDescription = "Changed Later",
            IntegrationConfiguration = new MicrosoftBillingIntegrationConfiguration("changed-account"),
            OneDriveFolder = new OneDriveFolder("new-drive", "New Drive", "new-folder", "/Changed"),
        };
        var records = new InMemoryInvoiceRecordRepository(record);
        var source = new FakeInvoiceSourceIntegration(
            BuildMatch(new DateOnly(2025, 7, 12), new Money(10m, "GBP"), "snapshot-source"));
        var oneDrive = new FakeOneDriveIntegration();

        await BuildProcessor(records, source, oneDrive, edited).ProcessDueAsync();

        var requestedConfiguration = Assert.Single(source.Requests).IntegrationConfiguration;
        Assert.True(requestedConfiguration is MicrosoftBillingIntegrationConfiguration billing &&
            billing.BillingAccountId == "test:billing:account");
        Assert.Equal("/Bills/Test", Assert.Single(oneDrive.Uploads).Destination.FolderPath);
        Assert.Contains("Test Invoice", Assert.Single(oneDrive.Uploads).FileName);
    }

    [Fact]
    public async Task ProcessDueAsync_DrivesRecordThroughRetrievedThenSavedToOneDrive_OnMatch()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var match = BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778");
        var source = new FakeInvoiceSourceIntegration(match);
        var oneDrive = new FakeOneDriveIntegration();

        var processor = BuildProcessor(records, source, oneDrive, config);

        var results = await processor.ProcessDueAsync();

        var success = Assert.Single(results);
        Assert.True(success is ProcessingSucceeded succeeded && succeeded.RecordId == dueRecord.Id);

        var saved = records.All.Single(r => r.Id == dueRecord.Id);
        if (saved.State is not SavedToOneDrive savedState)
        {
            Assert.Fail($"Expected SavedToOneDrive but was {saved.State}.");
            return;
        }

        Assert.Equal(new DateOnly(2025, 7, 12), savedState.ActualDetails.ActualInvoiceDate);
        Assert.Equal(new SourceInvoiceId("G152207778"), savedState.ActualDetails.SourceInvoiceId);
        Assert.Equal(
            "/drives/test-drive/items/test-folder-item/2025-07-12 Test Invoice G152207778 £10.00 exc.pdf",
            savedState.OneDriveDetails.OneDriveLocation);
    }

    [Fact]
    public async Task ProcessDueAsync_UploadsGeneratedFilenameAndPdfBytes_OnMatch()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var pdf = new byte[] { 1, 2, 3, 4 };
        var match = new InvoiceMatch(
            pdf,
            Actuals.Build(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778")));
        var oneDrive = new FakeOneDriveIntegration();

        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(match), oneDrive, config);

        await processor.ProcessDueAsync();

        var upload = Assert.Single(oneDrive.Uploads);
        Assert.Equal("/drives/test-drive/items/test-folder-item", upload.DestinationPath);
        Assert.Equal("2025-07-12 Test Invoice G152207778 £10.00 exc.pdf", upload.FileName);
        Assert.Equal(pdf, upload.Content);
    }

    [Fact]
    public async Task ProcessDueAsync_PersistsAfterEachStep_OnMatch()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new RecordingInvoiceRecordRepository(dueRecord);

        var match = BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778");
        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(match), new FakeOneDriveIntegration(), config);

        await processor.ProcessDueAsync();

        // Retrieved is persisted before the upload, SavedToOneDrive after it.
        Assert.Collection(
            records.Replaced,
            first => Assert.True(first.State is Retrieved, $"Expected Retrieved but was {first.State}."),
            second => Assert.True(second.State is SavedToOneDrive, $"Expected SavedToOneDrive but was {second.State}."));
    }

    [Fact]
    public async Task ProcessDueAsync_DoesNotCreateNextExpectedRecord_OnSuccess()
    {
        // Creating the next expected record is ExpectedRecordGenerator's job, run
        // separately before this processor in both Functions entry points - not
        // something DueInvoiceProcessor does itself. See the class remarks.
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var match = BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778");
        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(match), new FakeOneDriveIntegration(), config);

        await processor.ProcessDueAsync();

        var stored = Assert.Single(records.All);
        Assert.True(stored.State is SavedToOneDrive, $"Expected SavedToOneDrive but was {stored.State}.");
    }

    [Fact]
    public async Task ProcessDueAsync_ResumesRetrievedRecord_OnLaterRun()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12),
            new Money(10.00m, "GBP"),
            new SourceInvoiceId("G152207778"));
        var retrievedRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new Retrieved(actualDetails));
        var records = new InMemoryInvoiceRecordRepository(retrievedRecord);

        var source = new FakeInvoiceSourceIntegration(new InvoiceMatch([1, 2, 3], actualDetails));
        var oneDrive = new FakeOneDriveIntegration();
        var processor = BuildProcessor(records, source, oneDrive, config);

        var results = await processor.ProcessDueAsync();

        var success = Assert.Single(results);
        Assert.True(success is ProcessingSucceeded succeeded && succeeded.RecordId == retrievedRecord.Id);

        var saved = Assert.Single(records.All);
        Assert.True(saved.State is SavedToOneDrive, $"Expected SavedToOneDrive but was {saved.State}.");
        Assert.Single(oneDrive.Uploads);
    }

    [Fact]
    public async Task ProcessDueAsync_BuildsCriteriaFromRecordAndConfiguration()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), amountTolerance: 0.50m);
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var processor = BuildProcessor(records, source, new FakeOneDriveIntegration(), config);

        await processor.ProcessDueAsync();

        var criteria = Assert.Single(source.Requests);
        Assert.Equal(config.IntegrationConfiguration, criteria.IntegrationConfiguration);
        Assert.Equal(new DateOnly(2025, 7, 10), criteria.ExpectedDate);
        Assert.Equal(config.DateToleranceDays, criteria.DateToleranceDays);
        Assert.Equal(config.AmountMatchingCriteria, criteria.AmountMatchingCriteria);
    }

    [Fact]
    public async Task ProcessDueAsync_LeavesRecordExpected_WhenNoMatchWithinToleranceWindow()
    {
        // Expected 2025-07-14 + 5 day tolerance = deadline 2025-07-19, still ahead of today (2025-07-15).
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 14));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 14));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);
        var oneDrive = new FakeOneDriveIntegration();

        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic")), oneDrive, config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingNoMatch);
        var state = records.All.Single().State;
        Assert.True(
            state is Expected { LastDiagnostic: string diagnostic } && diagnostic == "test diagnostic",
            $"Expected Expected with LastDiagnostic 'test diagnostic' but was {state}.");
        Assert.Empty(oneDrive.Uploads);
    }

    [Fact]
    public async Task ProcessDueAsync_ClearsRetrievalErrorBackToExpected_WhenCleanPollFindsNoMatchWithinWindow()
    {
        // Expected 2025-07-14 + 5 day tolerance = deadline 2025-07-19, still ahead of today (2025-07-15).
        // A RetrievalError record polled successfully (no throw) with no match resets to Expected.
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 14));
        var erroredRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 14),
            state: new RetrievalError("earlier transient failure"));
        var records = new InMemoryInvoiceRecordRepository(erroredRecord);

        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic")), new FakeOneDriveIntegration(), config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingNoMatch);
        Assert.True(records.All.Single().State is Expected);
    }

    [Fact]
    public async Task ProcessDueAsync_MarksNotFound_WhenNoMatchOnToleranceDeadline()
    {
        // Expected 2025-07-10 + 5 day tolerance = deadline 2025-07-15, which is today: on or after → NotFound.
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);
        var oneDrive = new FakeOneDriveIntegration();

        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic")), oneDrive, config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingNotFound);
        Assert.Equal(new NotFound("test diagnostic"), records.All.Single().State);
        Assert.Empty(oneDrive.Uploads);
    }

    [Fact]
    public async Task ProcessDueAsync_MarksNotFoundDirectly_WhenFirstProcessedAfterToleranceWindow()
    {
        // Expected 2025-07-01 + 5 day tolerance = deadline 2025-07-06, already elapsed by today (2025-07-15).
        // A still-Expected record processed for the first time after its window goes straight to NotFound.
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 1));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 1), state: new Expected(Option.None));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic")), new FakeOneDriveIntegration(), config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingNotFound);
        Assert.True(records.All.Single().State is NotFound);
    }

    [Fact]
    public async Task ProcessDueAsync_MarksRetrievalError_WhenRetrievalThrows()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var source = new ThrowingSourceIntegration(
            failFor: new DateOnly(2025, 7, 10),
            otherwise: new NoInvoiceMatch("test diagnostic"));
        var processor = BuildProcessor(records, source, new FakeOneDriveIntegration(), config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFailed);
        var stored = records.All.Single();
        if (stored.State is not RetrievalError error)
        {
            Assert.Fail($"Expected RetrievalError but was {stored.State}.");
            return;
        }

        Assert.Equal("Simulated source failure.", error.ErrorMessage);
    }

    [Fact]
    public async Task ProcessDueAsync_RetriesRetrievalErrorRecord_AndSavesOnLaterMatch()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var erroredRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new RetrievalError("earlier transient failure"));
        var records = new InMemoryInvoiceRecordRepository(erroredRecord);

        var match = BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778");
        var oneDrive = new FakeOneDriveIntegration();
        var processor = BuildProcessor(records, new FakeInvoiceSourceIntegration(match), oneDrive, config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingSucceeded);
        Assert.True(records.All.Single(r => r.Id == erroredRecord.Id).State is SavedToOneDrive);
        Assert.Single(oneDrive.Uploads);
    }

    [Fact]
    public async Task ProcessDueAsync_LogsRunSummaryWithPerOutcomeCounts()
    {
        var savedConfig = Configurations.Build(id: new InvoiceConfigurationId("config-saved"), startDate: new DateOnly(2025, 7, 10));
        var notFoundConfig = Configurations.Build(id: new InvoiceConfigurationId("config-notfound"), startDate: new DateOnly(2025, 7, 1));
        var savedRecord = Records.Build(savedConfig, expectedDate: new DateOnly(2025, 7, 10));
        var notFoundRecord = Records.Build(notFoundConfig, expectedDate: new DateOnly(2025, 7, 1));
        var records = new InMemoryInvoiceRecordRepository(savedRecord, notFoundRecord);

        var source = new DateDrivenSourceIntegration(
            matches: new Dictionary<DateOnly, InvoiceSourceResult>
            {
                [new DateOnly(2025, 7, 10)] = BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "SRC-1"),
                [new DateOnly(2025, 7, 1)] = new NoInvoiceMatch("test diagnostic"),
            });
        var logger = new ListLogger<DueInvoiceProcessor>();

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(savedConfig, notFoundConfig),
            [source],
            new FakeOneDriveIntegration(),
            BuildFilename(),
            new FakeFreeAgentBillMatcher(),
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            logger);

        await processor.ProcessDueAsync();

        var summary = Assert.Single(logger.Messages, m => m.Contains("run complete"));
        Assert.Contains("1 saved", summary);
        Assert.Contains("0 no match yet", summary);
        Assert.Contains("1 not found", summary);
        Assert.Contains("0 failed", summary);
    }

    [Fact]
    public async Task ProcessDueAsync_IsolatesFailure_AndContinuesWithOtherRecords()
    {
        var failing = Configurations.Build(id: new InvoiceConfigurationId("config-failing"), startDate: new DateOnly(2025, 7, 1));
        var healthy = Configurations.Build(id: new InvoiceConfigurationId("config-healthy"), startDate: new DateOnly(2025, 7, 2));
        var failingRecord = Records.Build(failing, expectedDate: new DateOnly(2025, 7, 1));
        var healthyRecord = Records.Build(healthy, expectedDate: new DateOnly(2025, 7, 2));
        var records = new InMemoryInvoiceRecordRepository(failingRecord, healthyRecord);

        var source = new ThrowingSourceIntegration(
            failFor: new DateOnly(2025, 7, 1),
            otherwise: BuildMatch(new DateOnly(2025, 7, 3), new Money(10.00m, "GBP"), "SRC-1"));

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(failing, healthy),
            [source],
            new FakeOneDriveIntegration(),
            BuildFilename(),
            new FakeFreeAgentBillMatcher(),
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.Equal(2, results.Count);
        var failure = Assert.Single(results, r => r is ProcessingFailed);
        Assert.True(failure is ProcessingFailed failed && failed.RecordId == failingRecord.Id);
        Assert.Single(results, r => r is ProcessingSucceeded);
        Assert.True(records.All.Single(r => r.Id == failingRecord.Id).State is RetrievalError);
    }

    [Fact]
    public async Task ProcessDueAsync_ReconcilesFromOneDrive_WithoutCallingSourceOrUploading()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        // The source should never be consulted once OneDrive already has the file.
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration
        {
            NextSearchResult = new OneDriveMatch(
                new OneDriveDetails("/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item"),
                Actuals.Build(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778")),
                "matched by date and amount"),
        };

        var processor = BuildProcessor(records, source, oneDrive, config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingReconciled reconciled && reconciled.RecordId == dueRecord.Id);

        var stored = records.All.Single(r => r.Id == dueRecord.Id);
        if (stored.State is not ReconciledFromOneDrive state)
        {
            Assert.Fail($"Expected ReconciledFromOneDrive but was {stored.State}.");
            return;
        }

        Assert.Equal("/drives/test-drive/items/test-folder-item/existing.pdf", state.OneDriveDetails.OneDriveLocation);
        Assert.Equal(new DateOnly(2025, 7, 12), state.ActualDetails.ActualInvoiceDate);
        Assert.Equal("matched by date and amount", state.MatchReason);
        Assert.Equal(Today, DateOnly.FromDateTime(state.ReconciledAt.UtcDateTime));

        var searched = Assert.Single(oneDrive.Searches);
        Assert.Equal(config.OneDriveFolder.GraphPath, searched.DestinationPath);
        // The description is part of the reconciliation criteria so records for
        // different subscriptions sharing a folder don't match each other's files.
        Assert.Equal(config.InvoiceDescription, searched.Criteria.InvoiceDescription);
        Assert.Empty(source.Requests);
        Assert.Empty(oneDrive.Uploads);
    }

    [Fact]
    public async Task ProcessDueAsync_FallsThroughToSource_WhenNoOneDriveMatch()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var source = new FakeInvoiceSourceIntegration(
            BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778"));
        var oneDrive = new FakeOneDriveIntegration(); // default: no OneDrive match

        var processor = BuildProcessor(records, source, oneDrive, config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingSucceeded);
        Assert.Single(oneDrive.Searches);
        Assert.Single(source.Requests);
        Assert.True(records.All.Single(r => r.Id == dueRecord.Id).State is SavedToOneDrive);
        Assert.Single(oneDrive.Uploads);
    }

    [Fact]
    public async Task ProcessDueAsync_ReportsConflict_WhenMatchedBillHasMultipleItemsAndAMismatchedTotal()
    {
        // "Never guess which item to change" must not silently fall through to attaching as
        // though reconciled - a multi-item bill whose total doesn't match the invoice has to be
        // reported as a conflict, not attached with the wrong amount left in place.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: new FreeAgentAmountReconciliation(0.01m));
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var source = new FakeInvoiceSourceIntegration(
            BuildMatch(new DateOnly(2025, 7, 12), new Money(121.00m, "GBP"), "G152207778"));
        var oneDrive = new FakeOneDriveIntegration();

        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var itemA = new FreeAgentBillItem(
            new FreeAgentBillItemIdentity("https://api.sandbox.freeagent.com/v2/bill_items/1"), "Item A", new Money(50.00m, "GBP"));
        var itemB = new FreeAgentBillItem(
            new FreeAgentBillItemIdentity("https://api.sandbox.freeagent.com/v2/bill_items/2"), "Item B", new Money(50.00m, "GBP"));
        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, new DateOnly(2025, 7, 12), new DateOnly(2025, 8, 12),
            new Money(100.00m, "GBP"), new Money(0m, "GBP"), new Money(100.00m, "GBP"), Option.None,
            matching.Contact.Url, "REF-1", [itemA, itemB], Option.None);

        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var uploader = new FakeFreeAgentAttachmentUploader
        {
            Upload = (_, _, _) => throw new InvalidOperationException("Attachment must never be uploaded for a bill left in conflict."),
        };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            uploader,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFreeAgentConflict);
        var stored = records.All.Single(r => r.Id == dueRecord.Id);
        Assert.True(stored.State is FreeAgentError, $"Expected FreeAgentError but was {stored.State}.");
    }

    [Fact]
    public async Task ProcessDueAsync_SaveFork_EntersFreeAgentMatchExpected_WithoutEverWritingSavedToOneDrive()
    {
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new RecordingInvoiceRecordRepository(dueRecord);

        var source = new FakeInvoiceSourceIntegration(
            BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778"));
        var oneDrive = new FakeOneDriveIntegration();
        var matcher = new FakeFreeAgentBillMatcher { Result = new NoFreeAgentBillMatch("test diagnostic") };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFreeAgentConflict);
        Assert.DoesNotContain(records.Replaced, r => r.State is SavedToOneDrive);
        var stored = records.All.Single(r => r.Id == dueRecord.Id);
        Assert.True(
            stored.State is FreeAgentMatchExpected { LastMatchDiagnostic: string matchDiagnostic } &&
                matchDiagnostic == "test diagnostic",
            $"Expected FreeAgentMatchExpected with LastMatchDiagnostic 'test diagnostic' but was {stored.State}.");
        Assert.Single(matcher.Requests);

        // Save path already has the PDF bytes in hand, so no re-download is needed.
        Assert.Empty(oneDrive.Downloads);
    }

    [Fact]
    public async Task ProcessDueAsync_ReconcileFork_EntersFreeAgentMatchExpected_AndRedownloadsPdf()
    {
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var oneDrive = new FakeOneDriveIntegration
        {
            NextSearchResult = new OneDriveMatch(
                oneDriveDetails,
                Actuals.Build(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778")),
                "matched by date and amount"),
        };
        var matcher = new FakeFreeAgentBillMatcher { Result = new NoFreeAgentBillMatch("test diagnostic") };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFreeAgentConflict);
        var stored = records.All.Single(r => r.Id == dueRecord.Id);
        Assert.True(stored.State is FreeAgentMatchExpected, $"Expected FreeAgentMatchExpected but was {stored.State}.");
        Assert.Empty(source.Requests);
        Assert.Empty(oneDrive.Uploads);

        // Reconciliation never has PDF bytes in hand, so entering the FreeAgent stage
        // re-downloads the matched file.
        Assert.Equal(oneDriveDetails, Assert.Single(oneDrive.Downloads));
    }

    [Fact]
    public async Task ProcessDueAsync_ResumesFreeAgentMatchExpected_ByRedownloadingPdf_WithoutSourceOrOneDriveSearch()
    {
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var matchExpectedRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentMatchExpected(actualDetails, oneDriveDetails, Option.None));
        var records = new InMemoryInvoiceRecordRepository(matchExpectedRecord);

        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, actualDetails.ActualInvoiceDate, actualDetails.ActualInvoiceDate.AddDays(30),
            actualDetails.ActualAmount, new Money(0m, "GBP"), actualDetails.ActualAmount, Option.None,
            matching.Contact.Url, "REF-1", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var attachment = new FreeAgentAttachmentMetadata("invoice.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var uploader = new FakeFreeAgentAttachmentUploader { Upload = (_, _, _) => new FreeAgentAttachmentUploaded(attachment) };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration();

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            uploader,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingSucceeded succeeded && succeeded.RecordId == matchExpectedRecord.Id);
        Assert.True(records.All.Single(r => r.Id == matchExpectedRecord.Id).State is FreeAgentAttached);
        Assert.Equal(oneDriveDetails, Assert.Single(oneDrive.Downloads));

        // Dispatch skips retrieval/reconciliation entirely for a record already in the FreeAgent stage.
        Assert.Empty(source.Requests);
        Assert.Empty(oneDrive.Searches);
        Assert.Empty(oneDrive.Uploads);
    }

    [Fact]
    public async Task ProcessDueAsync_ResumesFreeAgentError_ByRedownloadingPdf()
    {
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var erroredRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentError(actualDetails, oneDriveDetails, "earlier failure", Option.None));
        var records = new InMemoryInvoiceRecordRepository(erroredRecord);

        var matcher = new FakeFreeAgentBillMatcher { Result = new NoFreeAgentBillMatch("test diagnostic") };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration();

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFreeAgentConflict);
        Assert.True(records.All.Single(r => r.Id == erroredRecord.Id).State is FreeAgentMatchExpected);
        Assert.Equal(oneDriveDetails, Assert.Single(oneDrive.Downloads));
        Assert.Empty(source.Requests);
        Assert.Empty(oneDrive.Searches);
    }

    [Fact]
    public async Task ProcessDueAsync_MarksFreeAgentError_WhenResumeDownloadThrows()
    {
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var matchExpectedRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentMatchExpected(actualDetails, oneDriveDetails, Option.None));
        var records = new InMemoryInvoiceRecordRepository(matchExpectedRecord);

        var oneDrive = new FakeOneDriveIntegration
        {
            DownloadException = new InvalidOperationException("The file has been deleted."),
        };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            new FakeFreeAgentBillMatcher(),
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFailed);
        var stored = records.All.Single(r => r.Id == matchExpectedRecord.Id);
        if (stored.State is not FreeAgentError error)
        {
            Assert.Fail($"Expected FreeAgentError but was {stored.State}.");
            return;
        }

        Assert.Contains("The file has been deleted.", error.ErrorMessage);
    }

    [Fact]
    public async Task ProcessDueAsync_MarksFreeAgentError_WhenReconciliationThrowsAfterBillMatched()
    {
        // A technical failure (for example a transient FreeAgent outage) reconciling or
        // attaching after the bill was already matched and persisted must not strand the
        // record in FreeAgentBillMatched/FreeAgentBillReconciled - both excluded from the
        // due query - or it can never be retried automatically again.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: new FreeAgentDateReconciliation(0),
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var matchExpectedRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentMatchExpected(actualDetails, oneDriveDetails, Option.None));
        var records = new InMemoryInvoiceRecordRepository(matchExpectedRecord);

        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, actualDetails.ActualInvoiceDate.AddDays(1), actualDetails.ActualInvoiceDate.AddDays(30),
            actualDetails.ActualAmount, new Money(0m, "GBP"), actualDetails.ActualAmount, Option.None,
            matching.Contact.Url, "REF-1", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var reconciler = new FakeFreeAgentBillReconciler
        {
            DateReconciliation = (_, _) => throw new InvalidOperationException("FreeAgent is temporarily unavailable."),
        };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration();

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            reconciler,
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFailed);
        var stored = records.All.Single(r => r.Id == matchExpectedRecord.Id);
        if (stored.State is not FreeAgentError error)
        {
            Assert.Fail($"Expected FreeAgentError but was {stored.State}.");
            return;
        }

        Assert.Contains("FreeAgent is temporarily unavailable.", error.ErrorMessage);
    }

    [Fact]
    public async Task ProcessDueAsync_PreservesSameBillUploadProof_WhenReconciliationFailsBeforeReachingAttachAgain()
    {
        // A retry that rematches the same bill an earlier attempt genuinely uploaded to must not
        // lose that proof just because this run's failure struck before the attach step was
        // reached again (e.g. a transient reconciliation-side outage) - the earlier upload is
        // still real and unaffected by this run's unrelated failure.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: new FreeAgentDateReconciliation(0),
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var priorAttachment = new FreeAgentAttachmentMetadata(
            "2025-07-12 Test Invoice G152207778 £10.00 exc.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var erroredRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentError(
                actualDetails, oneDriveDetails, "earlier verification failure",
                new FreeAgentAttemptedAttachment(billIdentity, priorAttachment)));
        var records = new InMemoryInvoiceRecordRepository(erroredRecord);

        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, actualDetails.ActualInvoiceDate.AddDays(1), actualDetails.ActualInvoiceDate.AddDays(30),
            actualDetails.ActualAmount, new Money(0m, "GBP"), actualDetails.ActualAmount, Option.None,
            matching.Contact.Url, "REF-1", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var reconciler = new FakeFreeAgentBillReconciler
        {
            DateReconciliation = (_, _) => throw new InvalidOperationException("FreeAgent is temporarily unavailable."),
        };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration { DownloadResult = [1, 2, 3] };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            reconciler,
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFailed);
        var stored = records.All.Single(r => r.Id == erroredRecord.Id);
        if (stored.State is not FreeAgentError error)
        {
            Assert.Fail($"Expected FreeAgentError but was {stored.State}.");
            return;
        }

        if (error.AttemptedAttachment is not FreeAgentAttemptedAttachment attemptedAttachment)
        {
            Assert.Fail("Expected AttemptedAttachment to preserve the prior same-bill proof despite this run's unrelated failure.");
            return;
        }

        Assert.Equal(billIdentity, attemptedAttachment.Bill);
        Assert.Equal(priorAttachment, attemptedAttachment.Attachment);
    }

    [Fact]
    public async Task ProcessDueAsync_PassesPersistedAttemptedAttachment_SoARetryCanRecogniseItsPriorAttachment()
    {
        // A prior run's attachment upload actually succeeded on FreeAgent's side, but the
        // record was left FreeAgentError because verification of the read-back failed - the
        // only case that persists FreeAgentError.AttemptedAttachment. The retry must pass that
        // exact persisted metadata back as expectedExisting so IFreeAgentAttachmentUploader can
        // recognise its own prior attachment instead of reporting
        // FreeAgentAttachmentUnexpectedExisting and getting permanently stuck.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var attemptedAttachment = new FreeAgentAttachmentMetadata(
            "2025-07-12 Test Invoice G152207778 £10.00 exc.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var erroredRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentError(
                actualDetails, oneDriveDetails, "verification failed on the prior attempt",
                new FreeAgentAttemptedAttachment(billIdentity, attemptedAttachment)));
        var records = new InMemoryInvoiceRecordRepository(erroredRecord);

        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, actualDetails.ActualInvoiceDate, actualDetails.ActualInvoiceDate.AddDays(30),
            actualDetails.ActualAmount, new Money(0m, "GBP"), actualDetails.ActualAmount, Option.None,
            matching.Contact.Url, "REF-1", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration { DownloadResult = [1, 2, 3] };

        var attached = new FreeAgentAttachmentMetadata("invoice.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var uploader = new FakeFreeAgentAttachmentUploader { Upload = (_, _, _) => new FreeAgentAttachmentUploaded(attached) };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            uploader,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingSucceeded succeeded && succeeded.RecordId == erroredRecord.Id);

        var expectedExisting = Assert.Single(uploader.ExpectedExistingRequests);
        if (expectedExisting is not FreeAgentAttachmentMetadata metadata)
        {
            Assert.Fail("Expected the persisted AttemptedAttachment to flow through as expectedExisting.");
            return;
        }

        Assert.Equal(attemptedAttachment, metadata);
    }

    [Fact]
    public async Task ProcessDueAsync_NeverReusesAttemptedAttachment_WhenRetryMatchesADifferentBill()
    {
        // Proof of an earlier upload is bound to the bill it was actually uploaded to. If a
        // later retry matches a different bill (the contact's bills changed, an earlier
        // ambiguous match resolved differently), that proof must never be presented as evidence
        // for the new bill - it was never uploaded there.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var originalBillIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var attemptedAttachment = new FreeAgentAttachmentMetadata(
            "2025-07-12 Test Invoice G152207778 £10.00 exc.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var erroredRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentError(
                actualDetails, oneDriveDetails, "verification failed on the prior attempt",
                new FreeAgentAttemptedAttachment(originalBillIdentity, attemptedAttachment)));
        var records = new InMemoryInvoiceRecordRepository(erroredRecord);

        var differentBillIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/2");
        var bill = new FreeAgentBillSnapshot(
            differentBillIdentity, FreeAgentBillStatus.Open, actualDetails.ActualInvoiceDate, actualDetails.ActualInvoiceDate.AddDays(30),
            actualDetails.ActualAmount, new Money(0m, "GBP"), actualDetails.ActualAmount, Option.None,
            matching.Contact.Url, "REF-2", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration { DownloadResult = [1, 2, 3] };

        var attached = new FreeAgentAttachmentMetadata("invoice.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var uploader = new FakeFreeAgentAttachmentUploader { Upload = (_, _, _) => new FreeAgentAttachmentUploaded(attached) };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            uploader,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingSucceeded);
        Assert.True(Assert.Single(uploader.ExpectedExistingRequests) is None);
    }

    [Fact]
    public async Task ProcessDueAsync_PreservesUploadProof_WhenPersistingFreeAgentAttachedFails()
    {
        // A technical failure persisting the terminal FreeAgentAttached state (after FreeAgent
        // itself already accepted and verified the upload) must not discard the proof that the
        // upload succeeded - otherwise the next retry finds the real attachment on the bill but
        // has no way to recognise it as its own.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var matchExpectedRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentMatchExpected(actualDetails, oneDriveDetails, Option.None));
        var records = new ThrowingOnReplaceStateRepository(matchExpectedRecord, state => state is FreeAgentAttached);

        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, actualDetails.ActualInvoiceDate, actualDetails.ActualInvoiceDate.AddDays(30),
            actualDetails.ActualAmount, new Money(0m, "GBP"), actualDetails.ActualAmount, Option.None,
            matching.Contact.Url, "REF-1", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration();

        var attached = new FreeAgentAttachmentMetadata("invoice.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var uploader = new FakeFreeAgentAttachmentUploader { Upload = (_, _, _) => new FreeAgentAttachmentUploaded(attached) };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            uploader,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFailed);
        var stored = records.All.Single(r => r.Id == matchExpectedRecord.Id);
        if (stored.State is not FreeAgentError error)
        {
            Assert.Fail($"Expected FreeAgentError but was {stored.State}.");
            return;
        }

        if (error.AttemptedAttachment is not FreeAgentAttemptedAttachment attemptedAttachment)
        {
            Assert.Fail("Expected AttemptedAttachment to preserve proof of the successful upload despite the persistence failure.");
            return;
        }

        Assert.Equal(billIdentity, attemptedAttachment.Bill);
        Assert.Equal(attached, attemptedAttachment.Attachment);
    }

    [Fact]
    public async Task ProcessDueAsync_NeverFabricatesExpectedExisting_WhenFreeAgentErrorHasNoAttemptedAttachment()
    {
        // A FreeAgentError with no AttemptedAttachment means either no attach was ever tried
        // (e.g. a bill-locked or reconciliation failure), or the attach outcome is unknown (a
        // generic technical exception). Retrying must always pass Option.None in that case -
        // never fabricate identity from what we're about to upload - so a bill's pre-existing,
        // unrelated attachment can never be mistaken for our own.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var actualDetails = Actuals.Build(
            new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), new SourceInvoiceId("G152207778"));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test-drive/items/test-folder-item/existing.pdf", "test-drive", "existing-item");
        var erroredRecord = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 10),
            state: new FreeAgentError(actualDetails, oneDriveDetails, "FreeAgent bill locked", Option.None));
        var records = new InMemoryInvoiceRecordRepository(erroredRecord);

        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, actualDetails.ActualInvoiceDate, actualDetails.ActualInvoiceDate.AddDays(30),
            actualDetails.ActualAmount, new Money(0m, "GBP"), actualDetails.ActualAmount, Option.None,
            matching.Contact.Url, "REF-1", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var source = new FakeInvoiceSourceIntegration(new NoInvoiceMatch("test diagnostic"));
        var oneDrive = new FakeOneDriveIntegration { DownloadResult = [1, 2, 3] };

        var attached = new FreeAgentAttachmentMetadata("invoice.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var uploader = new FakeFreeAgentAttachmentUploader { Upload = (_, _, _) => new FreeAgentAttachmentUploaded(attached) };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            uploader,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingSucceeded);
        Assert.True(Assert.Single(uploader.ExpectedExistingRequests) is None);
    }

    [Fact]
    public async Task ProcessDueAsync_NeverFabricatesExpectedExisting_OnAFirstAttachAttempt()
    {
        // A record's very first attach attempt (freshly matched via save_fork/reconcile_fork,
        // never having reached FreeAgentError) must always pass Option.None: any attachment
        // already on the bill can only belong to someone else, and must never be mistaken for
        // our own upload by a coincidental filename/size/content-type match.
        var matching = new FreeAgentBillMatching(
            new FreeAgentContact(new FreeAgentContactIdentity("https://api.sandbox.freeagent.com/v2/contacts/1"), "Test Contact"),
            DateReconciliation: Option.None,
            AmountReconciliation: Option.None);
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10), freeAgentMatching: matching);
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var source = new FakeInvoiceSourceIntegration(
            BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778"));
        var oneDrive = new FakeOneDriveIntegration();

        var billIdentity = new FreeAgentBillIdentity("https://api.sandbox.freeagent.com/v2/bills/1");
        var bill = new FreeAgentBillSnapshot(
            billIdentity, FreeAgentBillStatus.Open, new DateOnly(2025, 7, 12), new DateOnly(2025, 8, 12),
            new Money(10.00m, "GBP"), new Money(0m, "GBP"), new Money(10.00m, "GBP"), Option.None,
            matching.Contact.Url, "REF-1", [], Option.None);
        var matcher = new FakeFreeAgentBillMatcher { Result = new FreeAgentBillFound(bill) };
        var attached = new FreeAgentAttachmentMetadata("invoice.pdf", 3, "application/pdf", Today.ToDateTime(TimeOnly.MinValue));
        var uploader = new FakeFreeAgentAttachmentUploader { Upload = (_, _, _) => new FreeAgentAttachmentUploaded(attached) };

        var processor = new DueInvoiceProcessor(
            records,
            new FakeConfigurationRepository(config),
            [source],
            oneDrive,
            BuildFilename(),
            matcher,
            new FakeFreeAgentBillReconciler(),
            uploader,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingSucceeded);
        Assert.True(Assert.Single(uploader.ExpectedExistingRequests) is None);
    }

    [Fact]
    public async Task ProcessDueAsync_MarksRetrievalError_WhenOneDriveSearchThrows()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 10));
        var records = new InMemoryInvoiceRecordRepository(dueRecord);

        var source = new FakeInvoiceSourceIntegration(
            BuildMatch(new DateOnly(2025, 7, 12), new Money(10.00m, "GBP"), "G152207778"));
        var oneDrive = new FakeOneDriveIntegration
        {
            SearchException = new InvalidOperationException("Graph is unavailable."),
        };

        var processor = BuildProcessor(records, source, oneDrive, config);

        var results = await processor.ProcessDueAsync();

        Assert.True(Assert.Single(results) is ProcessingFailed);
        var stored = records.All.Single();
        if (stored.State is not RetrievalError error)
        {
            Assert.Fail($"Expected RetrievalError but was {stored.State}.");
            return;
        }

        Assert.Equal("Graph is unavailable.", error.ErrorMessage);
        // A search failure means we could not tell whether the file exists, so the
        // source and upload are not attempted.
        Assert.Empty(source.Requests);
        Assert.Empty(oneDrive.Uploads);
    }

    private static InvoiceMatch BuildMatch(DateOnly date, Money amount, string sourceInvoiceId) =>
        new([1, 2, 3], Actuals.Build(date, amount, new SourceInvoiceId(sourceInvoiceId)));

    private static DueInvoiceProcessor BuildProcessor(
        IInvoiceRecordRepository records,
        IInvoiceSourceIntegration source,
        IOneDriveIntegration oneDrive,
        params InvoiceConfiguration[] configurations) =>
        new(
            records,
            new FakeConfigurationRepository(configurations),
            [source],
            oneDrive,
            BuildFilename(),
            new FakeFreeAgentBillMatcher(),
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Today),
            NullLogger<DueInvoiceProcessor>.Instance);

    private static InvoiceFilename BuildFilename() =>
        new(new InvoiceFilenameSettings { Culture = CultureInfo.GetCultureInfo("en-GB") });

    /// <summary>Records the order of <see cref="ReplaceAsync"/> calls for step-persistence assertions.</summary>
    private sealed class RecordingInvoiceRecordRepository(params InvoiceRecord[] initial)
        : InMemoryInvoiceRecordRepository(initial)
    {
        private readonly List<InvoiceRecord> replaced = [];

        public IReadOnlyList<InvoiceRecord> Replaced => replaced;

        public override Task ReplaceAsync(InvoiceRecord record, CancellationToken cancellationToken = default)
        {
            replaced.Add(record);
            return base.ReplaceAsync(record, cancellationToken);
        }
    }

    /// <summary>Throws when asked to persist a record whose state matches <paramref name="failFor"/>, to exercise preserving already-known evidence across a persistence failure.</summary>
    private sealed class ThrowingOnReplaceStateRepository(InvoiceRecord initial, Func<InvoiceWorkflowState, bool> failFor)
        : InMemoryInvoiceRecordRepository(initial)
    {
        public override Task ReplaceAsync(InvoiceRecord record, CancellationToken cancellationToken = default) =>
            failFor(record.State)
                ? throw new InvalidOperationException("Simulated persistence failure.")
                : base.ReplaceAsync(record, cancellationToken);
    }

    /// <summary>Throws for a specific expected date, matching otherwise, to exercise failure isolation.</summary>
    private sealed class ThrowingSourceIntegration(DateOnly failFor, InvoiceSourceResult otherwise)
        : IInvoiceSourceIntegration
    {
        public IntegrationType IntegrationType => IntegrationType.MicrosoftBilling;

        public Task<InvoiceSourceResult> FindInvoiceAsync(
            InvoiceSearchCriteria criteria,
            CancellationToken cancellationToken = default) =>
            criteria.ExpectedDate == failFor
                ? throw new InvalidOperationException("Simulated source failure.")
                : Task.FromResult(otherwise);
    }

    /// <summary>Returns a preconfigured result per expected date, for multi-record runs.</summary>
    private sealed class DateDrivenSourceIntegration(IReadOnlyDictionary<DateOnly, InvoiceSourceResult> matches)
        : IInvoiceSourceIntegration
    {
        public IntegrationType IntegrationType => IntegrationType.MicrosoftBilling;

        public Task<InvoiceSourceResult> FindInvoiceAsync(
            InvoiceSearchCriteria criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(matches[criteria.ExpectedDate]);
    }

    /// <summary>Captures rendered log messages for asserting emitted telemetry.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages => messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
