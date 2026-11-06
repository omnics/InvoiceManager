using System.Text;
using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;
using NodaMoney;

namespace InvoiceManager.Integrations.FreeAgent.IntegrationTests;

/// <summary>
/// Creates one real bill in the FreeAgent sandbox for the duration of
/// <see cref="FreeAgentBillSandboxTests"/>, and always deletes it again afterwards.
/// </summary>
public sealed class SandboxBillFixture : IAsyncLifetime
{
    private FreeAgentSandbox? sandbox;
    private FreeAgentSandboxBills? bills;
    private FreeAgentContactIdentity? contact;
    private FreeAgentBillIdentity? bill;

    internal FreeAgentSandbox Sandbox => sandbox ?? throw new InvalidOperationException("Fixture not initialized.");

    public FreeAgentContactIdentity Contact => contact ?? throw new InvalidOperationException("Fixture not initialized.");

    public FreeAgentBillIdentity Bill => bill ?? throw new InvalidOperationException("Fixture not initialized.");

    public DateOnly DatedOn { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

    // An improbable amount, so no other bill on the same contact and date window can match it
    // and turn a real match into an ambiguous one.
    public decimal TotalValue { get; } = Random.Shared.Next(100_000, 999_999) / 100m;

    public string Reference { get; } = $"InvoiceManager-IntegrationTest-{Guid.NewGuid():N}"[..40];

    public async Task InitializeAsync()
    {
        sandbox = await FreeAgentSandbox.ConnectAsync();
        bills = new FreeAgentSandboxBills(sandbox);
        contact = await bills.GetAnyContactAsync();
        bill = await bills.CreateAsync(contact, DatedOn, TotalValue, Reference);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (bill is not null && bills is not null)
                await bills.DeleteAsync(bill);
        }
        finally
        {
            bills?.Dispose();
            sandbox?.Dispose();
        }
    }
}

/// <summary>
/// Opt-in: exercises bill matching and attachment upload against a real bill in the FreeAgent
/// sandbox, rather than the StubHttpMessageHandler responses FreeAgentBillMatcherTests and
/// FreeAgentAttachmentUploaderTests use - so a change in FreeAgent's bill listing filters, wire
/// format or attachment handling fails here instead of only being found in a deployed environment.
/// See <see cref="FreeAgentSandbox"/> for the prerequisites.
///
/// Excluded from CI by the same `Category=Integration` filter as every other opt-in sandbox test
/// in this repo (see AGENTS.md) - treat a green run as informative, not authoritative.
/// </summary>
[Trait("Category", "Integration")]
[Collection(FreeAgentSandboxCollection.Name)]
public sealed class FreeAgentBillSandboxTests(SandboxBillFixture fixture) : IClassFixture<SandboxBillFixture>
{
    [Fact]
    public async Task FindBillAsync_FindsTheSandboxBill_WhenDateAndAmountMatchExactly()
    {
        var matcher = new FreeAgentBillMatcher(fixture.Sandbox.ApiClient);

        var result = await matcher.FindBillAsync(Criteria(fixture.DatedOn, 0, fixture.TotalValue, 0m, "GBP"));

        if (result is not FreeAgentBillFound { Bill: var found })
        {
            Assert.Fail($"Expected FreeAgentBillFound but got {result}.");
            return;
        }

        Assert.Equal(fixture.Bill, found.Identity);
        Assert.Equal(fixture.Contact, found.ContactUrl);
        Assert.Equal(fixture.DatedOn, found.DatedOn);
        Assert.Equal(new Money(fixture.TotalValue, "GBP"), found.TotalValue);
        Assert.Equal(fixture.Reference, found.Reference);
    }

    [Theory]
    [InlineData(3, 3, "0.00", "0.00", "GBP", true)] // date at the edge of the tolerance window
    [InlineData(3, 2, "0.00", "0.00", "GBP", false)] // date one day outside the window
    [InlineData(-3, 3, "0.00", "0.00", "GBP", true)] // window applies on both sides
    [InlineData(0, 0, "0.01", "0.01", "GBP", true)] // amount at the edge of the tolerance
    [InlineData(0, 0, "0.01", "0.00", "GBP", false)] // amount one penny outside the tolerance
    [InlineData(0, 0, "0.00", "0.00", "USD", false)] // same amount, different currency
    public async Task FindBillAsync_AppliesDateAndAmountTolerances_AgainstTheSandboxBill(
        int expectedDateOffsetDays, int dateToleranceDays, string amountOffset, string amountTolerance, string currency, bool expectMatch)
    {
        var matcher = new FreeAgentBillMatcher(fixture.Sandbox.ApiClient);

        var result = await matcher.FindBillAsync(Criteria(
            fixture.DatedOn.AddDays(expectedDateOffsetDays),
            dateToleranceDays,
            fixture.TotalValue + decimal.Parse(amountOffset, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(amountTolerance, System.Globalization.CultureInfo.InvariantCulture),
            currency));

        if (expectMatch)
        {
            Assert.True(result is FreeAgentBillFound found && found.Bill.Identity == fixture.Bill,
                $"Expected the sandbox bill to be found but got {result}.");
        }
        else
        {
            Assert.True(result is NoFreeAgentBillMatch, $"Expected NoFreeAgentBillMatch but got {result}.");
        }
    }

    [Fact]
    public async Task UploadAsync_AttachesThePdf_AndRecognisesItOnTheNextRun()
    {
        var uploader = new FreeAgentAttachmentUploader(fixture.Sandbox.ApiClient);
        var pdf = MinimalPdf();
        var fileName = $"{fixture.Reference}.pdf";

        var first = await uploader.UploadAsync(fixture.Bill, pdf, fileName, Option.None);

        if (first is not FreeAgentAttachmentUploaded { New: var uploaded })
        {
            Assert.Fail($"Expected FreeAgentAttachmentUploaded but got {first}.");
            return;
        }

        Assert.Equal(fileName, uploaded.FileName);
        Assert.Equal(pdf.Length, uploaded.FileSizeBytes);

        // A repeat processing run must not upload (and so replace) the attachment again - this is
        // what keeps repeated runs from duplicating or churning FreeAgent attachments.
        var second = await uploader.UploadAsync(fixture.Bill, pdf, fileName, uploaded);

        Assert.True(second is FreeAgentAttachmentAlreadyCorrect, $"Expected FreeAgentAttachmentAlreadyCorrect but got {second}.");
    }

    private FreeAgentBillSearchCriteria Criteria(
        DateOnly expectedDate, int dateToleranceDays, decimal expectedAmount, decimal amountTolerance, string currency) =>
        new(fixture.Contact, "Sandbox test contact", expectedDate, dateToleranceDays, new Money(expectedAmount, currency), amountTolerance);

    private static byte[] MinimalPdf() => Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
        "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\n" +
        "trailer<</Root 1 0 R>>\n%%EOF\n");
}
