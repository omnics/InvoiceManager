using InvoiceManager.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvoiceManager.Core.Tests;

public sealed class InvoiceRecordResyncTests
{
    [Theory]
    [InlineData(typeof(Expected))]
    [InlineData(typeof(RetrievalError))]
    [InlineData(typeof(NotFound))]
    public async Task ResyncMostRecentAsync_RefreshesSnapshotAndResetsToExpected_ForEligibleStates(Type stateType)
    {
        var originalConfig = Configurations.Build(invoiceDescription: "Stale Description");
        var record = Records.Build(originalConfig, state: BuildState(stateType));
        var records = new InMemoryInvoiceRecordRepository(record);

        // The configuration was edited after the record was generated - its snapshot must not
        // change until a resync explicitly re-derives it.
        var updatedConfig = originalConfig with { InvoiceDescription = "Corrected Description" };
        var configurations = new FakeConfigurationRepository(updatedConfig);
        var resync = new InvoiceRecordResync(records, configurations, NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(originalConfig.Id, originalConfig.IntegrationType);

        Assert.True(result is ResyncSucceeded succeeded && succeeded.RecordId == record.Id);
        var stored = Assert.Single(records.All);
        Assert.True(stored.State is Expected, $"Expected fresh Expected but was {stored.State}.");
        Assert.Equal("Corrected Description", stored.ProcessingSnapshot.InvoiceDescription);
    }

    [Theory]
    [InlineData(typeof(Retrieved))]
    [InlineData(typeof(SavedToOneDrive))]
    public async Task ResyncMostRecentAsync_ReturnsNotEligible_ForStatesPastMatching(Type stateType)
    {
        var config = Configurations.Build();
        var record = Records.Build(config, state: BuildState(stateType));
        var records = new InMemoryInvoiceRecordRepository(record);
        var resync = new InvoiceRecordResync(
            records, new FakeConfigurationRepository(config), NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(config.Id, config.IntegrationType);

        Assert.True(result is ResyncNotEligible notEligible && notEligible.RecordId == record.Id);
        Assert.Equal(record.State, Assert.Single(records.All).State);
    }

    [Fact]
    public async Task ResyncMostRecentAsync_ReturnsNoRecordExists_WhenConfigurationHasNoRecord()
    {
        var config = Configurations.Build();
        var records = new InMemoryInvoiceRecordRepository();
        var resync = new InvoiceRecordResync(
            records, new FakeConfigurationRepository(config), NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(config.Id, config.IntegrationType);

        Assert.True(result is ResyncNoRecordExists);
    }

    [Fact]
    public async Task ResyncMostRecentAsync_ReturnsConfigurationNotFound_WhenConfigurationDoesNotExist()
    {
        var records = new InMemoryInvoiceRecordRepository();
        var resync = new InvoiceRecordResync(
            records, new FakeConfigurationRepository(), NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(
            new InvoiceConfigurationId("missing"), IntegrationType.MicrosoftBilling);

        Assert.True(result is ResyncConfigurationNotFound);
    }

    private static InvoiceWorkflowState BuildState(Type stateType)
    {
        var actualDetails = Actuals.Build(new DateOnly(2025, 7, 5));
        var oneDriveDetails = new OneDriveDetails(
            "/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");

        return stateType.Name switch
        {
            nameof(Expected) => new Expected(Option.None),
            nameof(RetrievalError) => new RetrievalError("earlier transient failure"),
            nameof(NotFound) => new NotFound("no invoice found within tolerance"),
            nameof(Retrieved) => new Retrieved(actualDetails),
            nameof(SavedToOneDrive) => new SavedToOneDrive(actualDetails, oneDriveDetails),
            _ => throw new ArgumentOutOfRangeException(nameof(stateType), stateType, "Unhandled state type in test."),
        };
    }
}
