using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.TestSupport;
using NodaMoney;

namespace InvoiceManager.Integrations.FreeAgent.Tests;

public sealed class FreeAgentBillMatcherTests
{
    private const string ContactUrl = "https://api.sandbox.freeagent.com/v2/contacts/1";
    private const string ContactDisplayName = "Test Contact Ltd";
    private const string BillUrl = "https://api.sandbox.freeagent.com/v2/bills/1";

    [Fact]
    public async Task FindBillAsync_MatchesOnAmountAndCurrency_WhenBothAgree()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillsPageJson(("GBP", "121.00", "REF-1"))),
                _ => JsonResponse(EmptyPageJson()),
            });
        var client = TestClientFactory.Create(handler);
        var matcher = new FreeAgentBillMatcher(client);

        var criteria = new FreeAgentBillSearchCriteria(
            new FreeAgentContactIdentity(ContactUrl),
            ContactDisplayName,
            new DateOnly(2026, 8, 1),
            3,
            new Money(121.00m, "GBP"),
            0.01m);

        var result = await matcher.FindBillAsync(criteria);

        Assert.True(result is FreeAgentBillFound, $"Expected FreeAgentBillFound but got {result}.");
    }

    [Fact]
    public async Task FindBillAsync_Matches_RegardlessOfBillReferenceText()
    {
        // The bill's reference is free text an operator typed in FreeAgent, sharing no words
        // with either the invoice configuration's description ("Microsoft 365 Business Basic")
        // or the source invoice's own identifier ("G172600804") - matching keys off contact +
        // date + amount only.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillsPageJson(("GBP", "121.00", "Recurring bill"))),
                _ => JsonResponse(EmptyPageJson()),
            });
        var client = TestClientFactory.Create(handler);
        var matcher = new FreeAgentBillMatcher(client);

        var criteria = new FreeAgentBillSearchCriteria(
            new FreeAgentContactIdentity(ContactUrl),
            ContactDisplayName,
            new DateOnly(2026, 8, 1),
            3,
            new Money(121.00m, "GBP"),
            0.01m);

        var result = await matcher.FindBillAsync(criteria);

        Assert.True(result is FreeAgentBillFound, $"Expected FreeAgentBillFound but got {result}.");
    }

    [Fact]
    public async Task FindBillAsync_RejectsAmountMatch_WhenCurrencyDiffers()
    {
        // Same numeric total (121.00) but in USD, not the expected GBP - must not match
        // just because the decimal amounts happen to agree.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillsPageJson(("USD", "121.00", "REF-1"))),
                _ => JsonResponse(EmptyPageJson()),
            });
        var client = TestClientFactory.Create(handler);
        var matcher = new FreeAgentBillMatcher(client);

        var criteria = new FreeAgentBillSearchCriteria(
            new FreeAgentContactIdentity(ContactUrl),
            ContactDisplayName,
            new DateOnly(2026, 8, 1),
            3,
            new Money(121.00m, "GBP"),
            0.01m);

        var result = await matcher.FindBillAsync(criteria);

        if (result is not NoFreeAgentBillMatch noMatch)
        {
            Assert.Fail($"Expected NoFreeAgentBillMatch but got {result}.");
            return;
        }

        Assert.Contains(ContactDisplayName, noMatch.Diagnostic);
        Assert.DoesNotContain(ContactUrl, noMatch.Diagnostic);
        Assert.Contains("121.00", noMatch.Diagnostic);
        Assert.Contains("USD", noMatch.Diagnostic);
        Assert.Contains("GBP", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindBillAsync_DiagnosticReportsNearestCandidate_WhenAmountChangedOutsideTolerance()
    {
        // Reproduces a FreeAgent-side price rise: a bill exists in the date window, but its
        // total no longer satisfies the configured amount tolerance.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillsPageJson(("GBP", "13.61", "Recurring bill"))),
                _ => JsonResponse(EmptyPageJson()),
            });
        var client = TestClientFactory.Create(handler);
        var matcher = new FreeAgentBillMatcher(client);

        var criteria = new FreeAgentBillSearchCriteria(
            new FreeAgentContactIdentity(ContactUrl),
            ContactDisplayName,
            new DateOnly(2026, 8, 1),
            3,
            new Money(11.59m, "GBP"),
            0.01m);

        var result = await matcher.FindBillAsync(criteria);

        if (result is not NoFreeAgentBillMatch noMatch)
        {
            Assert.Fail($"Expected NoFreeAgentBillMatch but got {result}.");
            return;
        }

        Assert.Contains(BillUrl, noMatch.Diagnostic);
        Assert.Contains("13.61", noMatch.Diagnostic);
        Assert.Contains("11.59", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindBillAsync_DiagnosticPrefersSameCurrencyCandidate_OverNumericallyCloserForeignCurrency()
    {
        // 99.00 USD is numerically closer to the expected 100.00 GBP than 105.00 GBP is, but
        // MatchesAmount already rejects USD candidates outright on currency - the diagnostic's
        // "nearest" candidate must not be picked by subtracting raw decimals across
        // currencies, or it reports a candidate that was never really in contention.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillsPageJson(("USD", "99.00", "REF-1"), ("GBP", "105.00", "REF-2"))),
                _ => JsonResponse(EmptyPageJson()),
            });
        var client = TestClientFactory.Create(handler);
        var matcher = new FreeAgentBillMatcher(client);

        var criteria = new FreeAgentBillSearchCriteria(
            new FreeAgentContactIdentity(ContactUrl),
            ContactDisplayName,
            new DateOnly(2026, 8, 1),
            3,
            new Money(100.00m, "GBP"),
            0.01m);

        var result = await matcher.FindBillAsync(criteria);

        if (result is not NoFreeAgentBillMatch noMatch)
        {
            Assert.Fail($"Expected NoFreeAgentBillMatch but got {result}.");
            return;
        }

        Assert.Contains("105.00", noMatch.Diagnostic);
        Assert.DoesNotContain("99.00", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindBillAsync_DiagnosticReportsNoBillsFound_WhenDateWindowIsEmpty()
    {
        var handler = new StubHttpMessageHandler((request, index) => JsonResponse(EmptyPageJson()));
        var client = TestClientFactory.Create(handler);
        var matcher = new FreeAgentBillMatcher(client);

        var criteria = new FreeAgentBillSearchCriteria(
            new FreeAgentContactIdentity(ContactUrl),
            ContactDisplayName,
            new DateOnly(2026, 8, 1),
            3,
            new Money(11.59m, "GBP"),
            0.01m);

        var result = await matcher.FindBillAsync(criteria);

        if (result is not NoFreeAgentBillMatch noMatch)
        {
            Assert.Fail($"Expected NoFreeAgentBillMatch but got {result}.");
            return;
        }

        Assert.Contains(ContactDisplayName, noMatch.Diagnostic);
        Assert.DoesNotContain(ContactUrl, noMatch.Diagnostic);
        Assert.Contains("No FreeAgent bill", noMatch.Diagnostic);
    }

    [Fact]
    public async Task FindBillAsync_DiagnosticFallsBackToContactUrl_WhenDisplayNameIsBlank()
    {
        var handler = new StubHttpMessageHandler((request, index) => JsonResponse(EmptyPageJson()));
        var client = TestClientFactory.Create(handler);
        var matcher = new FreeAgentBillMatcher(client);

        var criteria = new FreeAgentBillSearchCriteria(
            new FreeAgentContactIdentity(ContactUrl),
            "",
            new DateOnly(2026, 8, 1),
            3,
            new Money(11.59m, "GBP"),
            0.01m);

        var result = await matcher.FindBillAsync(criteria);

        if (result is not NoFreeAgentBillMatch noMatch)
        {
            Assert.Fail($"Expected NoFreeAgentBillMatch but got {result}.");
            return;
        }

        Assert.Contains(ContactUrl, noMatch.Diagnostic);
    }

    private static string BillsPageJson(params (string Currency, string TotalValue, string Reference)[] bills) =>
        $$"""
        {"bills": [{{string.Join(",", bills.Select(b => $$"""
        {
          "url": "{{BillUrl}}",
          "contact": "{{ContactUrl}}",
          "reference": "{{b.Reference}}",
          "dated_on": "2026-08-01",
          "due_on": "2026-08-30",
          "currency": "{{b.Currency}}",
          "total_value": "{{b.TotalValue}}",
          "paid_value": "0.00",
          "due_value": "{{b.TotalValue}}",
          "status": "Open",
          "bill_items": []
        }
        """))}}]}
        """;

    private static string EmptyPageJson() => """{"bills": []}""";

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
