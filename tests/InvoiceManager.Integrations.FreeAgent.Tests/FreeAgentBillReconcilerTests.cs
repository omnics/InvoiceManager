using InvoiceManager.Core.Integrations.FreeAgent;
using InvoiceManager.TestSupport;
using NodaMoney;

namespace InvoiceManager.Integrations.FreeAgent.Tests;

public sealed class FreeAgentBillReconcilerTests
{
    private const string BillUrl = "https://api.sandbox.freeagent.com/v2/bills/1";
    private const string OtherBillItemUrl = "https://api.sandbox.freeagent.com/v2/bill_items/999";
    private const string ItemUrl = "https://api.sandbox.freeagent.com/v2/bill_items/1";

    [Fact]
    public async Task ReconcileItemAmountAsync_RejectsItemNotOnBill_BeforeAnyMutationRequest()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                // GET the bill: its only item is ItemUrl, not OtherBillItemUrl.
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                _ => throw new InvalidOperationException("No mutation request should have been sent."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(OtherBillItemUrl),
            new Money(100m, "GBP"));

        Assert.True(result is FreeAgentItemNotOnBill, $"Expected FreeAgentItemNotOnBill but got {result}.");
        Assert.Single(handler.Requests); // only the GET, no PUT
    }

    [Fact]
    public async Task ReconcileItemAmountAsync_AcceptsFreeAgentsVatValues_WhenMutationSucceeds()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                1 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], totalValue: "125.50")),
                // Verified against an independent GET, not the PUT response body.
                2 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], totalValue: "125.50")),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(ItemUrl),
            new Money(125.50m, "GBP"));

        Assert.True(result is FreeAgentReconciled, $"Expected FreeAgentReconciled but got {result}.");
        var reconciled = ExtractReconciled(result);
        Assert.Equal(125.50m, reconciled.Bill.TotalValue.Amount);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
    }

    [Fact]
    public async Task ReconcileItemAmountAsync_ReturnsLocked_WhenLockedAndNoGuessExists()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Paid", items: [ItemUrl])),
                1 => LockedResponse(),
                2 => JsonResponse(BankAccountsJson()),
                _ => JsonResponse(ExplanationsJson([])),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(ItemUrl),
            new Money(125.50m, "GBP"));
        Assert.True(result is FreeAgentBillLocked, $"Expected FreeAgentBillLocked but got {result}.");
        var locked = ExtractLocked(result);
        Assert.Equal(FreeAgentLockReason.CachedTotalLocked, locked.Reason);
    }

    [Fact]
    public async Task ReconcileItemAmountAsync_ReturnsInterventionRequired_WhenExactlyOneGuessExists()
    {
        const string bankAccountUrl = "https://api.sandbox.freeagent.com/v2/bank_accounts/1";
        const string explanationUrl = "https://api.sandbox.freeagent.com/v2/bank_transaction_explanations/1";
        const string bankTransactionUrl = "https://api.sandbox.freeagent.com/v2/bank_transactions/1";

        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Paid", items: [ItemUrl])),
                1 => LockedResponse(),
                2 => JsonResponse(BankAccountsJson(bankAccountUrl)),
                3 => JsonResponse(ExplanationsJson(
                    [(explanationUrl, BillUrl, bankTransactionUrl, markedForReview: true)])),
                4 => JsonResponse(BillJson(status: "Paid", items: [ItemUrl])),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(ItemUrl),
            new Money(125.50m, "GBP"));

        Assert.True(result is FreeAgentPaymentInterventionRequired, $"Expected FreeAgentPaymentInterventionRequired but got {result}.");
        var intervention = ExtractIntervention(result);
        Assert.Equal(explanationUrl, intervention.Intervention.GuessExplanationUrl);
        Assert.Equal(bankTransactionUrl, intervention.Intervention.BankTransactionUrl);
        Assert.Equal(125.50m, intervention.Intervention.ProposedBillAmount.Amount);
        // Never deletes anything itself.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ReconcileItemAmountAsync_ReturnsRemoteRejected_WhenA422HasNoLockedFieldSignal()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                // A normal validation 422 (e.g. an invalid amount), not FreeAgent's proven
                // locked-field response - must never be reported as a lock.
                1 => new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent(
                        """{"errors": {"bill_items.0.total_value": [{"message": "is not a number"}]}}""",
                        System.Text.Encoding.UTF8, "application/json"),
                },
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(ItemUrl),
            new Money(125.50m, "GBP"));

        Assert.True(result is FreeAgentRemoteRejected, $"Expected FreeAgentRemoteRejected but got {result}.");
    }

    [Fact]
    public async Task ReconcileItemAmountAsync_ReturnsRemoteRejected_WhenTheLockedFieldNameAppearsWithoutLockedWording()
    {
        // Same field name FreeAgent's proven locked response uses, but a normal validation
        // message rather than "is locked" - must not be classified as a lock on field name alone.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                1 => new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent(
                        """{"errors": {"bill_items.total_value": [{"message": "must be a positive number"}]}}""",
                        System.Text.Encoding.UTF8, "application/json"),
                },
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(ItemUrl),
            new Money(125.50m, "GBP"));

        Assert.True(result is FreeAgentRemoteRejected, $"Expected FreeAgentRemoteRejected but got {result}.");
    }

    [Fact]
    public async Task ReconcileItemAmountAsync_ReturnsVerificationFailed_WhenReturnedItemTotalDoesNotMatchRequest()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                // FreeAgent returns 200 but the item's total_value is unchanged (121.00, not the
                // requested 125.50) - must not be trusted as a successful reconciliation.
                1 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], totalValue: "121.00")),
                2 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], totalValue: "121.00")),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(ItemUrl),
            new Money(125.50m, "GBP"));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
    }

    [Fact]
    public async Task ReconcileItemAmountAsync_ReturnsVerificationFailed_WhenPutResponseIsStaleAndIndependentReadDisagrees()
    {
        // The PUT response itself echoes the requested value (optimistic/stale), but a fresh,
        // independent GET reveals FreeAgent never actually persisted it - must not be trusted.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                1 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], totalValue: "125.50")),
                2 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], totalValue: "121.00")),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileItemAmountAsync(
            new FreeAgentBillIdentity(BillUrl),
            new FreeAgentBillItemIdentity(ItemUrl),
            new Money(125.50m, "GBP"));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
        Assert.Equal(3, handler.Requests.Count); // GET + PUT + independent verification GET
    }

    [Fact]
    public async Task ReconcileDateAsync_ReturnsRemoteRejected_WhenA422HasNoLockedFieldSignal()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                1 => new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent(
                        """{"errors": {"dated_on": [{"message": "is not a valid date"}]}}""",
                        System.Text.Encoding.UTF8, "application/json"),
                },
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileDateAsync(new FreeAgentBillIdentity(BillUrl), new DateOnly(2026, 8, 3));

        Assert.True(result is FreeAgentRemoteRejected, $"Expected FreeAgentRemoteRejected but got {result}.");
        Assert.Equal(2, handler.Requests.Count); // GET + failed PUT only, no re-GET
    }

    [Theory]
    [InlineData("""{"errors": {"dated_on": [{"message": "is not a valid date"}]}}""", "FreeAgent rejected the dated_on change: is not a valid date")]
    [InlineData("""{"errors": {"error": {"message": "Bill has payments"}}}""", "FreeAgent rejected the dated_on change: Bill has payments")]
    [InlineData("""{"errors": [{"message": "First"}, {"message": "Second"}, {"message": "First"}]}""", "FreeAgent rejected the dated_on change: First; Second")]
    [InlineData("""{"errors": {"dated_on": ["no message property"]}, "token": "secret-value"}""", "FreeAgent rejected the dated_on change.")]
    [InlineData("<html>secret-value</html>", "FreeAgent rejected the dated_on change.")]
    [InlineData("", "FreeAgent rejected the dated_on change.")]
    public async Task ReconcileDateAsync_ReportsOnlyFreeAgentsValidationMessages_WhenRejected(string errorBody, string expectedDetail)
    {
        var result = await RejectDateChangeAsync(errorBody);

        Assert.True(result is FreeAgentRemoteRejected rejected && rejected.Detail == expectedDetail, $"Unexpected result: {result}.");
    }

    [Fact]
    public async Task ReconcileDateAsync_CapsTheValidationMessageLength_WhenRejected()
    {
        var result = await RejectDateChangeAsync($$"""{"errors": [{"message": "{{new string('x', 1000)}}"}]}""");

        Assert.True(
            result is FreeAgentRemoteRejected rejected && rejected.Detail.Length == "FreeAgent rejected the dated_on change: ".Length + 300,
            $"Unexpected result: {result}.");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"bill": null}""")]
    public async Task ReconcileDateAsync_ReturnsVerificationFailed_WhenASuccessfulResponseHasNoBill(string body)
    {
        // Only a 422 is FreeAgent actually refusing the change. A 2xx without a bill leaves the
        // outcome unknown, so it must not be reported as a rejection.
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                1 => JsonResponse(body),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var reconciler = new FreeAgentBillReconciler(TestClientFactory.Create(handler));

        var result = await reconciler.ReconcileDateAsync(new FreeAgentBillIdentity(BillUrl), new DateOnly(2026, 8, 3));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
    }

    private static async Task<FreeAgentReconciliationResult> RejectDateChangeAsync(string errorBody)
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl])),
                1 => new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
                },
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var reconciler = new FreeAgentBillReconciler(TestClientFactory.Create(handler));

        return await reconciler.ReconcileDateAsync(new FreeAgentBillIdentity(BillUrl), new DateOnly(2026, 8, 3));
    }

    [Fact]
    public async Task ReconcileDateAsync_ReturnsVerificationFailed_WhenBillDateDoesNotReflectChange()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], datedOn: "2026-08-02", dueOn: "2026-08-30")),
                // FreeAgent's PUT succeeds and item URLs/due_on/status are preserved, but the
                // date itself was not actually changed - must not be trusted as reconciled.
                1 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], datedOn: "2026-08-02", dueOn: "2026-08-30")),
                2 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], datedOn: "2026-08-02", dueOn: "2026-08-30")),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileDateAsync(new FreeAgentBillIdentity(BillUrl), new DateOnly(2026, 8, 3));

        Assert.True(result is FreeAgentVerificationFailed, $"Expected FreeAgentVerificationFailed but got {result}.");
    }

    [Fact]
    public async Task ReconcileDateAsync_VerifiesItemUrlsPreserved_AfterChangingDate()
    {
        var handler = new StubHttpMessageHandler((request, index) =>
            index switch
            {
                0 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], datedOn: "2026-08-02", dueOn: "2026-08-30")),
                1 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], datedOn: "2026-08-03", dueOn: "2026-08-30")),
                2 => JsonResponse(BillJson(status: "Open", items: [ItemUrl], datedOn: "2026-08-03", dueOn: "2026-08-30")),
                _ => throw new InvalidOperationException("Unexpected request."),
            });
        var client = TestClientFactory.Create(handler);
        var reconciler = new FreeAgentBillReconciler(client);

        var result = await reconciler.ReconcileDateAsync(new FreeAgentBillIdentity(BillUrl), new DateOnly(2026, 8, 3));

        Assert.True(result is FreeAgentReconciled, $"Expected FreeAgentReconciled but got {result}.");
        var reconciled = ExtractReconciled(result);
        Assert.Equal(new DateOnly(2026, 8, 3), reconciled.Bill.DatedOn);
        Assert.Equal(new DateOnly(2026, 8, 30), reconciled.Bill.DueOn);
    }

    private static FreeAgentReconciled ExtractReconciled(FreeAgentReconciliationResult result) =>
        result switch
        {
            FreeAgentReconciled reconciled => reconciled,
            _ => throw new InvalidOperationException($"Expected FreeAgentReconciled but got {result}."),
        };

    private static FreeAgentBillLocked ExtractLocked(FreeAgentReconciliationResult result) =>
        result switch
        {
            FreeAgentBillLocked locked => locked,
            _ => throw new InvalidOperationException($"Expected FreeAgentBillLocked but got {result}."),
        };

    private static FreeAgentPaymentInterventionRequired ExtractIntervention(FreeAgentReconciliationResult result) =>
        result switch
        {
            FreeAgentPaymentInterventionRequired intervention => intervention,
            _ => throw new InvalidOperationException($"Expected FreeAgentPaymentInterventionRequired but got {result}."),
        };

    private static string BillJson(
        string status, IReadOnlyList<string> items, string totalValue = "121.00", string datedOn = "2026-08-01", string dueOn = "2026-08-30") =>
        $$"""
        {
          "bill": {
            "url": "{{BillUrl}}",
            "contact": "https://api.sandbox.freeagent.com/v2/contacts/1",
            "reference": "REF-1",
            "dated_on": "{{datedOn}}",
            "due_on": "{{dueOn}}",
            "currency": "GBP",
            "total_value": "{{totalValue}}",
            "paid_value": "0.00",
            "due_value": "{{totalValue}}",
            "status": "{{status}}",
            "bill_items": [{{string.Join(",", items.Select(url => $$"""{"url": "{{url}}", "bill": "{{BillUrl}}", "description": "Item", "total_value": "{{totalValue}}"}"""))}}]
          }
        }
        """;

    private static HttpResponseMessage LockedResponse() => new(System.Net.HttpStatusCode.UnprocessableEntity)
    {
        Content = new StringContent(
            """{"errors": {"bill_items.total_value": [{"message": "is locked and cannot be changed"}]}}""",
            System.Text.Encoding.UTF8, "application/json"),
    };

    private static string BankAccountsJson(params string[] urls) =>
        $$"""
        {"bank_accounts": [{{string.Join(",", urls.Select(u => $$"""{"url": "{{u}}"}"""))}}]}
        """;

    private static string ExplanationsJson(
        IReadOnlyList<(string Url, string PaidBill, string BankTransaction, bool markedForReview)> explanations) =>
        $$"""
        {"bank_transaction_explanations": [{{string.Join(",", explanations.Select(e =>
            $$"""{"url": "{{e.Url}}", "paid_bill": "{{e.PaidBill}}", "bank_transaction": "{{e.BankTransaction}}", "marked_for_review": {{e.markedForReview.ToString().ToLowerInvariant()}}, "is_locked": false, "is_deletable": true}"""))}}]}
        """;

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
