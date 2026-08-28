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
                actualDetails, oneDrive, new FreeAgentInterventionId("intervention-1")),
            nameof(Expected) => new Expected(Option.None),
            nameof(Retrieved) => new Retrieved(actualDetails),
            nameof(FreeAgentMatchExpected) => new FreeAgentMatchExpected(actualDetails, oneDrive, Option.None),
            nameof(FreeAgentBillMatched) => new FreeAgentBillMatched(actualDetails, oneDrive, bill),
            nameof(FreeAgentBillReconciled) => new FreeAgentBillReconciled(actualDetails, oneDrive, bill),
            _ => throw new ArgumentOutOfRangeException(nameof(stateType), stateType, "Unhandled state type in test."),
        };

        Assert.Equal(expectedBucket, InvoiceSyncOverview.Bucket(state));
    }
}
