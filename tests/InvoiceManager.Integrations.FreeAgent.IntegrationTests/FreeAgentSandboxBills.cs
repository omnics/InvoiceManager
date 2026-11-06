using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Integrations.FreeAgent.IntegrationTests;

/// <summary>
/// Test-only creation and deletion of FreeAgent sandbox bills. Deliberately not part of
/// FreeAgentApiClient: InvoiceManager itself never creates or deletes bills, so production code
/// must not gain that ability just so a test can set up its own data.
/// </summary>
internal sealed class FreeAgentSandboxBills : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly FreeAgentSandbox sandbox;

    public FreeAgentSandboxBills(FreeAgentSandbox sandbox)
    {
        this.sandbox = sandbox;
        httpClient = FreeAgentSandbox.CreateHttpClient();
        httpClient.BaseAddress = FreeAgentSandbox.ApiBaseUri;
    }

    /// <summary>Any contact in the sandbox company - the bill only needs a valid owner.</summary>
    public async Task<FreeAgentContactIdentity> GetAnyContactAsync(CancellationToken cancellationToken = default)
    {
        var contacts = await sandbox.ApiClient.SearchContactsPageAsync(page: 1, perPage: 1, cancellationToken);
        var url = contacts.Select(c => c.Url).OfType<Uri>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The FreeAgent sandbox company has no contacts. Create one in the sandbox before running this test.");
        return new FreeAgentContactIdentity(url);
    }

    public async Task<FreeAgentBillIdentity> CreateAsync(
        FreeAgentContactIdentity contact,
        DateOnly datedOn,
        decimal totalValue,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var category = await GetAnyAdminExpensesCategoryAsync(cancellationToken);
        var payload = new
        {
            bill = new
            {
                contact = contact.Url.OriginalString,
                reference,
                dated_on = datedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                due_on = datedOn.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                bill_items = new[]
                {
                    new
                    {
                        category,
                        description = reference,
                        total_value = totalValue.ToString("0.00", CultureInfo.InvariantCulture),
                    },
                },
            },
        };

        using var request = await CreateRequestAsync(HttpMethod.Post, "bills", cancellationToken);
        request.Content = JsonContent.Create(payload);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "creating a bill", cancellationToken);

        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var url = body.RootElement.GetProperty("bill").GetProperty("url").GetString()
            ?? throw new InvalidOperationException("FreeAgent's bill creation response did not include the bill URL.");
        return new FreeAgentBillIdentity(url);
    }

    public async Task DeleteAsync(FreeAgentBillIdentity bill, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, bill.Url.OriginalString, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "deleting a bill", cancellationToken);
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<string> GetAnyAdminExpensesCategoryAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "categories", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "listing categories", cancellationToken);

        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return body.RootElement.GetProperty("admin_expenses_categories").EnumerateArray()
            .Select(category => category.GetProperty("url").GetString())
            .OfType<string>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The FreeAgent sandbox company has no admin expenses categories.");
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await sandbox.TokenProvider.AcquireTokenAsync(cancellationToken));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        // Unlike FreeAgentApiClient, this test-only helper includes the response body: these are
        // sandbox validation errors about the test's own request, and the body is the only way to
        // see which field FreeAgent rejected.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"FreeAgent sandbox request failed while {action}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }
}
