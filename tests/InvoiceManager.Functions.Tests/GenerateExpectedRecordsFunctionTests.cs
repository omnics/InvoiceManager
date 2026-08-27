using System.Globalization;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations;
using InvoiceManager.Core.Repositories;
using InvoiceManager.Functions.Functions;
using InvoiceManager.TestSupport;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NodaMoney;

namespace InvoiceManager.Functions.Tests;

public sealed class GenerateExpectedRecordsFunctionTests
{
    // A time before every configuration's start date, so generation runs but no
    // record is yet due for processing.
    private static readonly DateOnly BeforeAnyRecord = new(2025, 1, 1);

    [Fact]
    public async Task TimerFunction_CreatesExpectedRecords_ForEachActiveConfiguration()
    {
        var config1 = Configurations.Build(id: new InvoiceConfigurationId("config-1"), startDate: new DateOnly(2025, 7, 1));
        var config2 = Configurations.Build(id: new InvoiceConfigurationId("config-2"), startDate: new DateOnly(2025, 8, 1));
        var recordRepo = new InMemoryInvoiceRecordRepository();
        var function = BuildTimerFunction(recordRepo, BeforeAnyRecord, new NoInvoiceMatch(), config1, config2);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        Assert.Equal(2, recordRepo.All.Count);
        Assert.Contains(recordRepo.All, r => r.ConfigurationId == config1.Id && r.ExpectedDate == new DateOnly(2025, 7, 1));
        Assert.Contains(recordRepo.All, r => r.ConfigurationId == config2.Id && r.ExpectedDate == new DateOnly(2025, 8, 1));
    }

    [Fact]
    public async Task TimerFunction_DoesNotCreateRecords_WhenNoActiveConfigurationsExist()
    {
        var recordRepo = new InMemoryInvoiceRecordRepository();
        var function = BuildTimerFunction(recordRepo, BeforeAnyRecord, new NoInvoiceMatch());

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        Assert.Empty(recordRepo.All);
    }

    [Fact]
    public async Task TimerFunction_ProcessesRemainingConfigurations_WhenOneFails()
    {
        var failing = Configurations.Build(id: new InvoiceConfigurationId("config-failing"), startDate: new DateOnly(2025, 7, 1));
        var healthy = Configurations.Build(id: new InvoiceConfigurationId("config-healthy"), startDate: new DateOnly(2025, 8, 1));
        var recordRepo = new ThrowingInvoiceRecordRepository(
            failing.Id, new InvalidOperationException("Simulated repository failure."));
        var function = BuildTimerFunction(recordRepo, BeforeAnyRecord, new NoInvoiceMatch(), failing, healthy);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        var record = Assert.Single(recordRepo.All);
        Assert.Equal(healthy.Id, record.ConfigurationId);
    }

    [Fact]
    public async Task TimerFunction_ProcessesDueRecord_ThroughToSavedToOneDrive()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("config-due"), startDate: new DateOnly(2025, 7, 1));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 1));
        var recordRepo = new InMemoryInvoiceRecordRepository(dueRecord);
        var match = new InvoiceMatch(
            [1, 2, 3],
            Actuals.Build(new DateOnly(2025, 7, 2), new Money(10.00m, "GBP"), new SourceInvoiceId("G1")));
        var function = BuildTimerFunction(recordRepo, new DateOnly(2025, 7, 15), match, config);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        var processed = recordRepo.All.Single(r => r.Id == dueRecord.Id);
        Assert.True(processed.State is SavedToOneDrive, $"Expected SavedToOneDrive but was {processed.State}.");
    }

    // Locks in the contract DueInvoiceProcessor's remarks describe: it no longer creates the
    // next expected record itself, so a successful match this run produces no next record
    // until GenerateForAllActiveAsync runs again on a *later* invocation of the same function -
    // covering the orchestration between the two components, not just each in isolation.
    [Fact]
    public async Task TimerFunction_CreatesNextExpectedRecord_OnlyOnASubsequentInvocation()
    {
        var config = Configurations.Build(id: new InvoiceConfigurationId("config-due"), startDate: new DateOnly(2025, 7, 1));
        var dueRecord = Records.Build(config, expectedDate: new DateOnly(2025, 7, 1));
        var recordRepo = new InMemoryInvoiceRecordRepository(dueRecord);
        var match = new InvoiceMatch(
            [1, 2, 3],
            Actuals.Build(new DateOnly(2025, 7, 2), new Money(10.00m, "GBP"), new SourceInvoiceId("G1")));
        var function = BuildTimerFunction(recordRepo, new DateOnly(2025, 7, 15), match, config);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        Assert.Single(recordRepo.All);
        Assert.True(recordRepo.All.Single().State is SavedToOneDrive);

        await function.RunAsync(new TimerInfo(), CancellationToken.None);

        Assert.Equal(2, recordRepo.All.Count);
        var next = recordRepo.All.Single(r => r.State is Expected);
        Assert.Equal(new DateOnly(2025, 8, 2), next.ExpectedDate);
    }

    private static GenerateExpectedRecordsTimer BuildTimerFunction(
        IInvoiceRecordRepository recordRepo,
        DateOnly today,
        InvoiceSourceResult sourceResult,
        params InvoiceConfiguration[] configurations)
    {
        var configRepo = new FakeConfigurationRepository(configurations);
        var generator = new ExpectedRecordGenerator(recordRepo, configRepo, NullLogger<ExpectedRecordGenerator>.Instance);
        var processor = new DueInvoiceProcessor(
            recordRepo,
            configRepo,
            [new FakeInvoiceSourceIntegration(sourceResult)],
            new FakeOneDriveIntegration(),
            new InvoiceFilename(new InvoiceFilenameSettings { Culture = CultureInfo.GetCultureInfo("en-GB") }),
            new FakeFreeAgentBillMatcher(),
            new FakeFreeAgentBillReconciler(),
            new FakeFreeAgentAttachmentUploader(),
            new InMemoryFreeAgentInterventionRepository(),
            new FixedTimeProvider(today),
            NullLogger<DueInvoiceProcessor>.Instance);
        return new GenerateExpectedRecordsTimer(
            generator,
            processor,
            NullLogger<GenerateExpectedRecordsTimer>.Instance);
    }
}
