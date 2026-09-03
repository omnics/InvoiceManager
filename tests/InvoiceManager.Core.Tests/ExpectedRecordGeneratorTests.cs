using InvoiceManager.Core.Repositories;
using InvoiceManager.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvoiceManager.Core.Tests;

public sealed class ExpectedRecordGeneratorTests
{
    // Cycle 1: no records exist → creates expected record with config start date.
    [Fact]
    public async Task GenerateAsync_CreatesExpectedRecord_WhenNoRecordsExist()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 1));
        var records = new InMemoryInvoiceRecordRepository();
        var generator = BuildGenerator(records);

        await generator.GenerateAsync(config);

        var record = Assert.Single(records.All);
        Assert.Equal(config.Id, record.ConfigurationId);
        Assert.Equal(new DateOnly(2025, 7, 1), record.ExpectedDate);
        Assert.True(record.State is Expected);
        Assert.Equal(config.InvoiceDescription, record.ProcessingSnapshot.InvoiceDescription);
        Assert.Equal(config.AmountMatchingCriteria, record.ProcessingSnapshot.AmountMatchingCriteria);
        Assert.Equal(config.DefaultVatMode, record.ProcessingSnapshot.VatMode);
        Assert.Equal(config.DateToleranceDays, record.ProcessingSnapshot.DateToleranceDays);
    }

    // Cycle 2: most recent record is SavedToOneDrive → creates next expected record using actual date + frequency.
    [Fact]
    public async Task GenerateAsync_CreatesNextExpectedRecord_WhenMostRecentIsSavedToOneDrive()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 1));
        var existing = Records.Build(config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new SavedToOneDrive(
                Actuals.Build(new DateOnly(2025, 7, 5)),
                new OneDriveDetails("/drives/test/root:/Bills/Test/invoice.pdf", "test-drive", "invoice-item")));
        var records = new InMemoryInvoiceRecordRepository(existing);
        var generator = BuildGenerator(records);

        await generator.GenerateAsync(config);

        Assert.Equal(2, records.All.Count);
        var next = records.All.Single(r => r.State is Expected);
        Assert.Equal(new DateOnly(2025, 8, 5), next.ExpectedDate);
    }

    // Cycle 3: most recent record is in progress (before SavedToOneDrive) → does nothing.
    [Fact]
    public async Task GenerateAsync_DoesNothing_WhenMostRecentIsInProgress()
    {
        var config = Configurations.Build();
        var existing = Records.Build(config, state: new Retrieved(
            Actuals.Build(new DateOnly(2025, 7, 5))));
        var records = new InMemoryInvoiceRecordRepository(existing);
        var generator = BuildGenerator(records);

        await generator.GenerateAsync(config);

        Assert.Single(records.All);
    }

    // Cycle 4: expected record already exists for the calculated date → idempotent, does nothing.
    [Fact]
    public async Task GenerateAsync_DoesNothing_WhenExpectedRecordAlreadyExistsForDate()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 1));
        var existing = Records.Build(config,
            expectedDate: new DateOnly(2025, 7, 1),
            state: new Expected(Option.None));
        var records = new InMemoryInvoiceRecordRepository(existing);
        var generator = BuildGenerator(records);

        await generator.GenerateAsync(config);

        Assert.Single(records.All);
    }

    [Fact]
    public async Task GenerateForAllActiveAsync_CreatesRecordForEveryActiveConfiguration()
    {
        var config1 = Configurations.Build(id: new InvoiceConfigurationId("config-1"), startDate: new DateOnly(2025, 7, 1));
        var config2 = Configurations.Build(id: new InvoiceConfigurationId("config-2"), startDate: new DateOnly(2025, 8, 1));
        var records = new InMemoryInvoiceRecordRepository();
        var generator = BuildGenerator(records, config1, config2);

        var results = await generator.GenerateForAllActiveAsync();

        Assert.Equal(2, records.All.Count);
        Assert.All(results, r => Assert.True(r is GenerationSucceeded));
    }

    [Fact]
    public async Task GenerateForAllActiveAsync_ContinuesWithRemainingConfigurations_WhenOneFails()
    {
        var failing = Configurations.Build(id: new InvoiceConfigurationId("config-failing"), startDate: new DateOnly(2025, 7, 1));
        var healthy = Configurations.Build(id: new InvoiceConfigurationId("config-healthy"), startDate: new DateOnly(2025, 8, 1));
        var records = new ThrowingInvoiceRecordRepository(
            failing.Id, new InvalidOperationException("Simulated repository failure."));
        var generator = BuildGenerator(records, failing, healthy);

        var results = await generator.GenerateForAllActiveAsync();

        var record = Assert.Single(records.All);
        Assert.Equal(healthy.Id, record.ConfigurationId);
        Assert.Equal(2, results.Count);
        var failure = Assert.Single(results, r => r is GenerationFailed);
        Assert.True(failure is GenerationFailed failed
            && failed.ConfigurationId == failing.Id
            && failed.Exception is InvalidOperationException);
        Assert.Single(results, r => r is GenerationSucceeded);
    }

    [Fact]
    public async Task GenerateForAllActiveAsync_PropagatesCancellation()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 1));
        var records = new ThrowingInvoiceRecordRepository(config.Id, new OperationCanceledException());
        var generator = BuildGenerator(records, config);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => generator.GenerateForAllActiveAsync());
    }

    private static ExpectedRecordGenerator BuildGenerator(
        IInvoiceRecordRepository records,
        params InvoiceConfiguration[] configurations) =>
        new(records,
            new FakeConfigurationRepository(configurations),
            NullLogger<ExpectedRecordGenerator>.Instance);
}
