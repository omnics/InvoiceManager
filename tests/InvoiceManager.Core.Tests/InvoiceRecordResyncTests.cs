using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NodaMoney;

namespace InvoiceManager.Core.Tests;

public sealed class InvoiceRecordResyncTests
{
    private static readonly InvoiceConfigurationActor Actor = new("actor-oid", "Test Actor");
    private static readonly DateTimeOffset Now = new(2025, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(typeof(Expected))]
    [InlineData(typeof(RetrievalError))]
    [InlineData(typeof(NotFound))]
    [InlineData(typeof(FreeAgentError))]
    [InlineData(typeof(FreeAgentMatchExpected))]
    public async Task ResyncMostRecentAsync_RefreshesSnapshotAndResetsToExpected_ForEligibleStates(Type stateType)
    {
        var originalConfig = Configurations.Build(invoiceDescription: "Stale Description");
        var record = Records.Build(originalConfig, state: BuildState(stateType));
        var records = new InMemoryInvoiceRecordRepository(record);

        // The configuration was edited after the record was generated - its snapshot must not
        // change until a resync explicitly re-derives it.
        var updatedConfig = originalConfig with { InvoiceDescription = "Corrected Description" };
        var configurations = new FakeConfigurationRepository(updatedConfig);
        var resync = new InvoiceRecordResync(
            records,
            configurations,
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Now),
            NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(originalConfig.Id, originalConfig.IntegrationType, Actor);

        Assert.True(result is ResyncSucceeded succeeded && succeeded.RecordId == record.Id);
        var stored = Assert.Single(records.All);
        Assert.True(stored.State is Expected, $"Expected fresh Expected but was {stored.State}.");
        Assert.Equal("Corrected Description", stored.ProcessingSnapshot.InvoiceDescription);
    }

    [Fact]
    public async Task ResyncMostRecentAsync_SupersedesThePendingIntervention_ForFreeAgentInterventionPending()
    {
        var config = Configurations.Build();
        var interventionId = new FreeAgentInterventionId("intervention-1");
        var record = Records.Build(config, state: BuildInterventionPendingState(interventionId));
        var records = new InMemoryInvoiceRecordRepository(record);
        var interventions = new InMemoryFreeAgentInterventionRepository();
        await interventions.CreateAsync(BuildIntervention(interventionId, record.Id));
        var resync = new InvoiceRecordResync(
            records,
            new FakeConfigurationRepository(config),
            interventions,
            new FixedTimeProvider(Now),
            NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(config.Id, config.IntegrationType, Actor);

        Assert.True(result is ResyncSucceeded);
        var intervention = Assert.Single(interventions.Created);
        Assert.Equal(FreeAgentGuessInterventionStatus.Superseded, intervention.Status);
        Assert.True(Assert.Single(records.All).State is Expected);
    }

    [Theory]
    [InlineData(typeof(Retrieved))]
    [InlineData(typeof(SavedToOneDrive))]
    [InlineData(typeof(FreeAgentBillMatched))]
    [InlineData(typeof(FreeAgentBillReconciled))]
    public async Task ResyncMostRecentAsync_ReturnsNotEligible_ForStatesResolvedWithinTheSameRun(Type stateType)
    {
        var config = Configurations.Build();
        var record = Records.Build(config, state: BuildState(stateType));
        var records = new InMemoryInvoiceRecordRepository(record);
        var resync = new InvoiceRecordResync(
            records,
            new FakeConfigurationRepository(config),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Now),
            NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(config.Id, config.IntegrationType, Actor);

        Assert.True(result is ResyncNotEligible notEligible && notEligible.RecordId == record.Id);
        Assert.Equal(record.State, Assert.Single(records.All).State);
    }

    [Fact]
    public async Task ResyncMostRecentAsync_ReturnsNoRecordExists_WhenConfigurationHasNoRecord()
    {
        var config = Configurations.Build();
        var records = new InMemoryInvoiceRecordRepository();
        var resync = new InvoiceRecordResync(
            records,
            new FakeConfigurationRepository(config),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Now),
            NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(config.Id, config.IntegrationType, Actor);

        Assert.True(result is ResyncNoRecordExists);
    }

    [Fact]
    public async Task ResyncMostRecentAsync_ReturnsConfigurationNotFound_WhenConfigurationDoesNotExist()
    {
        var records = new InMemoryInvoiceRecordRepository();
        var resync = new InvoiceRecordResync(
            records,
            new FakeConfigurationRepository(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(Now),
            NullLogger<InvoiceRecordResync>.Instance);

        var result = await resync.ResyncMostRecentAsync(
            new InvoiceConfigurationId("missing"), IntegrationType.MicrosoftBilling, Actor);

        Assert.True(result is ResyncConfigurationNotFound);
    }

    private static readonly OneDriveDetails OneDrive = new(
        "/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item");

    private static InvoiceWorkflowState BuildState(Type stateType)
    {
        var actualDetails = Actuals.Build(new DateOnly(2025, 7, 5));

        return stateType.Name switch
        {
            nameof(Expected) => new Expected(Option.None),
            nameof(RetrievalError) => new RetrievalError("earlier transient failure"),
            nameof(NotFound) => new NotFound("no invoice found within tolerance"),
            nameof(Retrieved) => new Retrieved(actualDetails),
            nameof(SavedToOneDrive) => new SavedToOneDrive(actualDetails, OneDrive),
            nameof(FreeAgentError) => new FreeAgentError(actualDetails, OneDrive, "reconciliation failed", Option.None),
            nameof(FreeAgentMatchExpected) => new FreeAgentMatchExpected(actualDetails, OneDrive, Option.None),
            nameof(FreeAgentBillMatched) => new FreeAgentBillMatched(
                actualDetails, OneDrive, new FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1")),
            nameof(FreeAgentBillReconciled) => new FreeAgentBillReconciled(
                actualDetails, OneDrive, new FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1")),
            _ => throw new ArgumentOutOfRangeException(nameof(stateType), stateType, "Unhandled state type in test."),
        };
    }

    private static InvoiceWorkflowState BuildInterventionPendingState(FreeAgentInterventionId interventionId) =>
        new FreeAgentInterventionPending(Actuals.Build(new DateOnly(2025, 7, 5)), OneDrive, interventionId);

    private static FreeAgentGuessIntervention BuildIntervention(FreeAgentInterventionId id, InvoiceRecordId recordId) =>
        new(
            id,
            recordId,
            new FreeAgentBillIdentity("https://api.freeagent.com/v2/bills/1"),
            new FreeAgentBillItemIdentity("https://api.freeagent.com/v2/bills/1/items/1"),
            "https://api.freeagent.com/v2/bank_transactions/1",
            "https://api.freeagent.com/v2/explanations/1",
            new Money(50.00m, "GBP"),
            new Money(60.00m, "GBP"),
            "Guess removed a bank transaction now needed for this bill.",
            Now,
            FreeAgentGuessInterventionStatus.Pending);
}
