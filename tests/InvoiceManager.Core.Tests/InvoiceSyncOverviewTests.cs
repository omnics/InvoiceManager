using InvoiceManager.TestSupport;

namespace InvoiceManager.Core.Tests;

public sealed class InvoiceSyncOverviewTests
{
    [Fact]
    public async Task GetRowsAsync_ShowsOnlyTheCurrentRow_WhenTheCurrentRecordIsAlreadyComplete()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var completed = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new SavedToOneDrive(
                Actuals.Build(new DateOnly(2025, 7, 1)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item")));
        var records = new InMemoryInvoiceRecordRepository(completed);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        var row = Assert.Single(rows);
        Assert.Equal(InvoiceSyncBucket.Complete, row.Bucket);
        Assert.Equal(new DateOnly(2025, 7, 1), row.Date);
        Assert.True(row.IsActualDate);
    }

    [Fact]
    public async Task GetRowsAsync_ShowsBothRows_WhenTheCurrentRecordIsNotYetComplete()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var completed = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 6, 1),
            state: new SavedToOneDrive(
                Actuals.Build(new DateOnly(2025, 6, 1)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item")));
        var current = Records.Build(
            config, expectedDate: new DateOnly(2025, 7, 1), state: new RetrievalError("transient failure"));
        var records = new InMemoryInvoiceRecordRepository(completed, current);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2025, 7, 1), rows[0].Date);
        Assert.Equal(InvoiceSyncBucket.NeedsAttention, rows[0].Bucket);
        Assert.Equal(new DateOnly(2025, 6, 1), rows[1].Date);
        Assert.Equal(InvoiceSyncBucket.Complete, rows[1].Bucket);
    }

    [Fact]
    public async Task GetRowsAsync_ShowsEveryNonCompleteRecord_NotJustTheMostRecentOne()
    {
        // Regression test for issue #135: several periods can pile up stuck (e.g. repeatedly
        // failing FreeAgent matching) between the last completed record and the current one -
        // all of them must be visible, not just the single most recent record.
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var completed = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 5, 1),
            state: new FreeAgentAttached(
                Actuals.Build(new DateOnly(2025, 5, 1)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item"),
                new Integrations.FreeAgent.FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1"),
                new Integrations.FreeAgent.FreeAgentAttachmentMetadata(
                    "invoice.pdf", 1024, "application/pdf", DateTimeOffset.UtcNow)));
        var stuckJune = Records.Build(
            config, expectedDate: new DateOnly(2025, 6, 1), state: new FreeAgentMatchExpected(
                Actuals.Build(new DateOnly(2025, 6, 1)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item"),
                Option.None));
        var stuckJuly = Records.Build(
            config, expectedDate: new DateOnly(2025, 7, 1), state: new FreeAgentMatchExpected(
                Actuals.Build(new DateOnly(2025, 7, 1)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item"),
                Option.None));
        var current = Records.Build(config, expectedDate: new DateOnly(2025, 8, 1), state: new Expected(Option.None));
        var records = new InMemoryInvoiceRecordRepository(completed, stuckJune, stuckJuly, current);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.Equal(
            [new DateOnly(2025, 8, 1), new DateOnly(2025, 7, 1), new DateOnly(2025, 6, 1), new DateOnly(2025, 5, 1)],
            rows.Select(r => r.Date));
    }

    [Fact]
    public async Task GetRowsAsync_OnlyOffersResync_OnTheConfigurationsActualMostRecentRecord()
    {
        // InvoiceRecordResync.ResyncMostRecentAsync always acts on a configuration's single most
        // recent record regardless of which row's button was clicked, so - now that several
        // non-complete rows can be shown for one configuration - only the row that's actually the
        // most recent record may report CanResync; an older stuck row must not, or clicking its
        // Resync button would silently mutate a different, newer record instead.
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var stuckJune = Records.Build(
            config, expectedDate: new DateOnly(2025, 6, 1), state: new RetrievalError("transient failure"));
        var stuckJuly = Records.Build(
            config, expectedDate: new DateOnly(2025, 7, 1), state: new RetrievalError("transient failure"));
        var records = new InMemoryInvoiceRecordRepository(stuckJune, stuckJuly);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        var july = Assert.Single(rows, r => r.Date == new DateOnly(2025, 7, 1));
        var june = Assert.Single(rows, r => r.Date == new DateOnly(2025, 6, 1));
        Assert.True(july.CanResync);
        Assert.False(june.CanResync);
    }

    [Fact]
    public async Task GetRowsAsync_OffersResync_OnTheMostRecentRecord_EvenWhenItIsAlreadyComplete()
    {
        // The overall most recent record can be complete while an older record is still stuck
        // (e.g. a later period matched cleanly while an earlier one didn't) - in that case the
        // completed record is still "most recent" and the older stuck one must not be resyncable.
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var stuckJune = Records.Build(
            config, expectedDate: new DateOnly(2025, 6, 1), state: new RetrievalError("transient failure"));
        var completedJuly = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new SavedToOneDrive(
                Actuals.Build(new DateOnly(2025, 7, 1)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item")));
        var records = new InMemoryInvoiceRecordRepository(stuckJune, completedJuly);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        var june = Assert.Single(rows, r => r.Date == new DateOnly(2025, 6, 1));
        Assert.False(june.CanResync);
    }

    [Fact]
    public async Task GetRowsAsync_OmitsAConfiguration_ThatHasNeverHadARecordGenerated()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("never-generated"));
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)),
            new InMemoryInvoiceRecordRepository());

        var rows = await overview.GetRowsAsync();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetRowsAsync_SortsAcrossConfigurations_ByDateDescending()
    {
        var configA = Configurations.Build(id: new InvoiceConfigurationId("a"));
        var configB = Configurations.Build(id: new InvoiceConfigurationId("b"));
        var older = Records.Build(configA, expectedDate: new DateOnly(2025, 6, 1));
        var newer = Records.Build(configB, expectedDate: new DateOnly(2025, 7, 1));
        var records = new InMemoryInvoiceRecordRepository(older, newer);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(configA, configB)), records);

        var rows = await overview.GetRowsAsync();

        Assert.Equal([newer.ConfigurationId, older.ConfigurationId], rows.Select(r => r.ConfigurationId));
    }

    [Theory]
    [InlineData(typeof(SavedToOneDrive), InvoiceSyncBucket.Complete)]
    [InlineData(typeof(ReconciledFromOneDrive), InvoiceSyncBucket.Complete)]
    [InlineData(typeof(FreeAgentAttached), InvoiceSyncBucket.Complete)]
    [InlineData(typeof(NotFound), InvoiceSyncBucket.NeedsAttention)]
    [InlineData(typeof(RetrievalError), InvoiceSyncBucket.NeedsAttention)]
    [InlineData(typeof(FreeAgentError), InvoiceSyncBucket.NeedsAttention)]
    [InlineData(typeof(FreeAgentInterventionPending), InvoiceSyncBucket.NeedsAttention)]
    [InlineData(typeof(Expected), InvoiceSyncBucket.InProgress)]
    [InlineData(typeof(Retrieved), InvoiceSyncBucket.InProgress)]
    [InlineData(typeof(FreeAgentMatchExpected), InvoiceSyncBucket.InProgress)]
    [InlineData(typeof(FreeAgentBillMatched), InvoiceSyncBucket.InProgress)]
    [InlineData(typeof(FreeAgentBillReconciled), InvoiceSyncBucket.InProgress)]
    public void Bucket_GroupsEveryWorkflowState_IntoTheExpectedBucket(Type stateType, InvoiceSyncBucket expectedBucket)
    {
        var actualDetails = Actuals.Build();
        var oneDrive = new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        var bill = new Integrations.FreeAgent.FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1");

        InvoiceWorkflowState state = stateType.Name switch
        {
            nameof(SavedToOneDrive) => new SavedToOneDrive(actualDetails, oneDrive),
            nameof(ReconciledFromOneDrive) => new ReconciledFromOneDrive(
                actualDetails, oneDrive, "matched existing file", DateTimeOffset.UtcNow),
            nameof(FreeAgentAttached) => new FreeAgentAttached(
                actualDetails, oneDrive, bill,
                new Integrations.FreeAgent.FreeAgentAttachmentMetadata(
                    "invoice.pdf", 1024, "application/pdf", DateTimeOffset.UtcNow)),
            nameof(NotFound) => new NotFound("no invoice found within tolerance"),
            nameof(RetrievalError) => new RetrievalError("transient failure"),
            nameof(FreeAgentError) => new FreeAgentError(actualDetails, oneDrive, "reconciliation failed", Option.None),
            nameof(FreeAgentInterventionPending) => new FreeAgentInterventionPending(
                actualDetails, oneDrive, bill, new FreeAgentInterventionId("intervention-1"), Option.None),
            nameof(Expected) => new Expected(Option.None),
            nameof(Retrieved) => new Retrieved(actualDetails),
            nameof(FreeAgentMatchExpected) => new FreeAgentMatchExpected(actualDetails, oneDrive, Option.None),
            nameof(FreeAgentBillMatched) => new FreeAgentBillMatched(actualDetails, oneDrive, bill),
            nameof(FreeAgentBillReconciled) => new FreeAgentBillReconciled(actualDetails, oneDrive, bill),
            _ => throw new ArgumentOutOfRangeException(nameof(stateType), stateType, "Unhandled state type in test."),
        };

        Assert.Equal(expectedBucket, InvoiceSyncOverview.Bucket(state));
    }

    [Fact]
    public async Task GetRowsAsync_ExposesOneDriveDetails_ForARowThatDownloadedTheInvoice()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var oneDrive = new OneDriveDetails(
            "/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        var record = Records.Build(
            config, expectedDate: new DateOnly(2025, 7, 1), state: new SavedToOneDrive(Actuals.Build(), oneDrive));
        var records = new InMemoryInvoiceRecordRepository(record);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.True(Assert.Single(rows).OneDrive is OneDriveDetails found && found == oneDrive);
    }

    [Fact]
    public async Task GetRowsAsync_ReportsNoOneDriveDetails_ForARowThatHasNotDownloadedAnything()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var record = Records.Build(config, expectedDate: new DateOnly(2025, 7, 1), state: new Expected(Option.None));
        var records = new InMemoryInvoiceRecordRepository(record);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.True(Assert.Single(rows).OneDrive is None);
    }

    [Fact]
    public async Task GetRowsAsync_ExposesTheFreeAgentBill_ForARowThatHasMatchedOne()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var oneDrive = new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        var bill = new Integrations.FreeAgent.FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1");
        var record = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new FreeAgentBillMatched(Actuals.Build(), oneDrive, bill));
        var records = new InMemoryInvoiceRecordRepository(record);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.True(Assert.Single(rows).FreeAgentBill is Integrations.FreeAgent.FreeAgentBillIdentity found && found == bill);
    }

    [Fact]
    public async Task GetRowsAsync_ReportsNoFreeAgentBill_ForARowThatHasNotMatchedOneYet()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var oneDrive = new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        var record = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new FreeAgentMatchExpected(Actuals.Build(), oneDrive, Option.None));
        var records = new InMemoryInvoiceRecordRepository(record);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.True(Assert.Single(rows).FreeAgentBill is None);
    }

    [Fact]
    public async Task GetRowsAsync_ExposesTheFreeAgentBill_ForAFreeAgentErrorWithAnAttemptedAttachment()
    {
        // The upload itself succeeded before this run errored (e.g. read-back verification
        // failed) - the bill it uploaded to is still worth surfacing on this Needs-attention row.
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var oneDrive = new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        var bill = new Integrations.FreeAgent.FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1");
        var record = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new FreeAgentError(
                Actuals.Build(),
                oneDrive,
                "verification failed",
                new FreeAgentErrorBillContext(
                    bill,
                    new Integrations.FreeAgent.FreeAgentAttachmentMetadata(
                        "invoice.pdf", 1024, "application/pdf", DateTimeOffset.UtcNow))));
        var records = new InMemoryInvoiceRecordRepository(record);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.True(Assert.Single(rows).FreeAgentBill is Integrations.FreeAgent.FreeAgentBillIdentity found && found == bill);
    }

    [Fact]
    public async Task GetRowsAsync_ExposesTheFreeAgentBill_ForAFreeAgentErrorWithAKnownBillButNoAttemptedAttachment()
    {
        // A lock, rejection, or reconciliation failure knows exactly which bill it was acting on
        // even though no upload was ever attempted (or completed) - that identity must not be
        // lost just because there's no attachment proof to go with it.
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var oneDrive = new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        var bill = new Integrations.FreeAgent.FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1");
        var record = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new FreeAgentError(Actuals.Build(), oneDrive, "FreeAgent bill locked", new FreeAgentErrorBillContext(bill, Option.None)));
        var records = new InMemoryInvoiceRecordRepository(record);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.True(Assert.Single(rows).FreeAgentBill is Integrations.FreeAgent.FreeAgentBillIdentity found && found == bill);
    }

    [Fact]
    public async Task GetRowsAsync_ReportsNoFreeAgentBill_ForAFreeAgentErrorWithNoKnownBill()
    {
        // A re-download failure resuming a fresh FreeAgent stage, before matching ever found a
        // bill in the first place.
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var oneDrive = new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        var record = Records.Build(
            config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new FreeAgentError(Actuals.Build(), oneDrive, "Could not re-download the invoice.", Option.None));
        var records = new InMemoryInvoiceRecordRepository(record);
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.True(Assert.Single(rows).FreeAgentBill is None);
    }

    [Theory]
    [InlineData(typeof(RetrievalError), "RetrievalError")]
    [InlineData(typeof(FreeAgentInterventionPending), "FreeAgentInterventionPending")]
    public async Task GetRowsAsync_ReportsTheExactUnderlyingStateName_NotTheUnionWrapperTypeName(
        Type stateType, string expectedStateName)
    {
        // InvoiceWorkflowState is a generated union wrapper struct - State.GetType().Name would
        // return "InvoiceWorkflowState" for every row regardless of which case it holds, so
        // StateName must come from an explicit switch instead of reflection over State itself.
        var oneDrive = new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");
        InvoiceWorkflowState state = stateType.Name switch
        {
            nameof(RetrievalError) => new RetrievalError("transient failure"),
            nameof(FreeAgentInterventionPending) => new FreeAgentInterventionPending(
                Actuals.Build(), oneDrive, new Integrations.FreeAgent.FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1"), new FreeAgentInterventionId("intervention-1"), Option.None),
            _ => throw new ArgumentOutOfRangeException(nameof(stateType), stateType, "Unhandled state type in test."),
        };
        var config = Configurations.Build(id: new InvoiceConfigurationId("acme"));
        var records = new InMemoryInvoiceRecordRepository(Records.Build(config, state: state));
        var overview = new InvoiceSyncOverview(
            new InvoiceConfigurationService(new FakeConfigurationRepository(config)), records);

        var rows = await overview.GetRowsAsync();

        Assert.Equal(expectedStateName, Assert.Single(rows).StateName);
    }
}
