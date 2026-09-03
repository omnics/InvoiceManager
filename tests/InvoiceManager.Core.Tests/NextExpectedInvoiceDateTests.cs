using InvoiceManager.Core;
using InvoiceManager.TestSupport;

namespace InvoiceManager.Core.Tests;

public sealed class NextExpectedInvoiceDateTests
{
    private const string OneDriveLocation = "/drives/test/root:/Bills/Test/invoice.pdf";
    private const string DriveId = "test-drive";
    private const string ItemId = "invoice-item";

    [Fact]
    public void CalculateNext_ReturnsStartDate_WhenNoRecordsExist()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));

        var result = NextExpectedInvoiceDate.CalculateNext(config, Option.None);

        Assert.Equal(new DateOnly(2025, 7, 10), ExpectedDate(result));
    }

    [Fact]
    public void CalculateNext_ReturnsActualDatePlusFrequency_WhenMostRecentRecordIsSaved()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var mostRecent = Records.Build(config, state: new SavedToOneDrive(
            Actuals.Build(new DateOnly(2026, 6, 10)),
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId)));

        var result = NextExpectedInvoiceDate.CalculateNext(config, mostRecent);

        Assert.Equal(new DateOnly(2026, 7, 10), ExpectedDate(result));
    }

    [Fact]
    public void CalculateNext_ReturnsInProgress_WhenMostRecentRecordIsBeforeSaved()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var mostRecent = Records.Build(config, state: new Expected(Option.None));

        var result = NextExpectedInvoiceDate.CalculateNext(config, mostRecent);

        Assert.True(IsInProgress(result));
    }

    [Fact]
    public void CalculateNext_ReturnsInProgress_WhenMostRecentRecordIsRetrieved()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var mostRecent = Records.Build(config, state: new Retrieved(
            Actuals.Build(new DateOnly(2026, 6, 10))));

        var result = NextExpectedInvoiceDate.CalculateNext(config, mostRecent);

        Assert.True(IsInProgress(result));
    }

    [Fact]
    public void CalculateNext_ReturnsActualDatePlusFrequency_WhenMostRecentRecordIsReconciled()
    {
        var config = Configurations.Build(startDate: new DateOnly(2025, 7, 10));
        var mostRecent = Records.Build(config, state: new ReconciledFromOneDrive(
            Actuals.Build(new DateOnly(2026, 6, 10)),
            new OneDriveDetails(OneDriveLocation, DriveId, ItemId),
            "matched by date and amount",
            new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero)));

        var result = NextExpectedInvoiceDate.CalculateNext(config, mostRecent);

        Assert.Equal(new DateOnly(2026, 7, 10), ExpectedDate(result));
    }

    private static DateOnly ExpectedDate(NextExpectedDateResult result) => result switch
    {
        NextExpectedDate next => next.Date,
        InvoiceInProgress => throw new Xunit.Sdk.XunitException(
            "Expected NextExpectedDate but got InvoiceInProgress."),
    };

    private static bool IsInProgress(NextExpectedDateResult result) => result switch
    {
        InvoiceInProgress => true,
        NextExpectedDate => false,
    };
}
