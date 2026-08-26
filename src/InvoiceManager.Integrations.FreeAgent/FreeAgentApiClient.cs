using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InvoiceManager.Infrastructure.FreeAgentAuthorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceManager.Integrations.FreeAgent;

/// <summary>The result of a FreeAgent API call that could be rejected because a field is locked.</summary>
internal sealed record FreeAgentApiResult<T>(T? Value, bool IsLocked, string? LockedFieldDetail, HttpStatusCode StatusCode)
{
    public bool Succeeded => Value is not null;
}

/// <summary>
/// Internal HTTP wrapper around the FreeAgent v2 REST API. Enforces the
/// sandbox/production host allowlist at construction time (production code can
/// never be pointed at an unrecognised host), attaches a bearer token via
/// <see cref="IFreeAgentTokenProvider"/>, and translates the proven 422
/// locked-field response into a typed result at this exact boundary rather than
/// letting callers parse FreeAgent's error shape themselves.
/// </summary>
internal sealed class FreeAgentApiClient
{
    // snake_case matches FreeAgent's wire format exactly (dated_on, total_value, ...), so every
    // FreeAgentWireModels property maps without its own JsonPropertyName - see that file for the
    // couple of properties whose C# name doesn't match the wire field 1:1, which still need one.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient;
    private readonly IFreeAgentTokenProvider tokenProvider;
    private readonly ILogger<FreeAgentApiClient> logger;

    public FreeAgentApiClient(
        HttpClient httpClient,
        IFreeAgentTokenProvider tokenProvider,
        IOptions<FreeAgentOptions> freeAgentOptions,
        ILogger<FreeAgentApiClient> logger)
    {
        this.httpClient = httpClient;
        this.tokenProvider = tokenProvider;
        this.logger = logger;

        var environment = freeAgentOptions.Value.Environment;
        httpClient.BaseAddress = FreeAgentHosts.ApiBaseUri(environment);
    }

    public async Task<IReadOnlyList<BillWire>> GetBillsPageAsync(
        DateOnly fromDate, DateOnly toDate, string contactUrl, int page, int perPage, CancellationToken cancellationToken)
    {
        var url =
            $"bills?nested_bill_items=true" +
            $"&from_date={fromDate:yyyy-MM-dd}&to_date={toDate:yyyy-MM-dd}" +
            $"&contact={Uri.EscapeDataString(contactUrl)}" +
            $"&page={page}&per_page={perPage}";

        using var response = await SendAsync(HttpMethod.Get, url, content: null, cancellationToken);
        await EnsureSuccessAsync(response, "listing bills", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BillsResponseWire>(SerializerOptions, cancellationToken);
        return body?.Bills ?? [];
    }

    public async Task<BillWire> GetBillAsync(string billUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{billUrl}?nested_bill_items=true", content: null, cancellationToken);
        await EnsureSuccessAsync(response, "reading a bill", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BillResponseWire>(SerializerOptions, cancellationToken);
        return body?.Bill ?? throw new InvalidOperationException("FreeAgent returned no bill body.");
    }

    public async Task<FreeAgentApiResult<BillWire>> PutBillDateAsync(
        string billUrl, DateOnly datedOn, CancellationToken cancellationToken)
    {
        var payload = new { bill = new { dated_on = datedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } };
        return await PutBillAsync(billUrl, payload, cancellationToken);
    }

    public async Task<FreeAgentApiResult<BillWire>> PutBillItemAmountAsync(
        string billUrl, string itemUrl, decimal totalValue, CancellationToken cancellationToken)
    {
        var payload = new
        {
            bill = new
            {
                bill_items = new[]
                {
                    new { url = itemUrl, total_value = totalValue.ToString("0.00", CultureInfo.InvariantCulture) },
                },
            },
        };
        return await PutBillAsync(billUrl, payload, cancellationToken);
    }

    private async Task<FreeAgentApiResult<BillWire>> PutBillAsync(string billUrl, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Put, billUrl, content, cancellationToken);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (ExtractLockedFieldDetail(errorBody) is { } lockedDetail)
                return new FreeAgentApiResult<BillWire>(default, IsLocked: true, lockedDetail, response.StatusCode);

            // A 422 without the proven locked-field signal is a normal validation rejection
            // (e.g. an invalid date/amount), not a lock - report it as a non-locked, valueless
            // result so the caller translates it to a remote-rejected outcome, never a lock.
            return new FreeAgentApiResult<BillWire>(default, IsLocked: false, null, response.StatusCode);
        }

        await EnsureSuccessAsync(response, "updating a bill", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BillResponseWire>(SerializerOptions, cancellationToken);
        return new FreeAgentApiResult<BillWire>(body?.Bill, IsLocked: false, null, response.StatusCode);
    }

    public async Task<AttachmentWire> PostAttachmentAsync(
        string billUrl, byte[] pdfBytes, string fileName, CancellationToken cancellationToken)
    {
        var payload = new
        {
            attachment = new
            {
                data = Convert.ToBase64String(pdfBytes),
                file_name = fileName,
                // FreeAgent's accepted attachment content_type values are image/png, image/x-png,
                // image/jpeg, image/jpg, image/gif, and application/x-pdf - the standard
                // "application/pdf" MIME type is not among them and is rejected with 400.
                content_type = "application/x-pdf",
            },
        };
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Put, $"{billUrl}/attachment", content, cancellationToken);
        await EnsureSuccessAsync(response, "uploading a bill attachment", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BillResponseWire>(SerializerOptions, cancellationToken);
        return body?.Bill?.Attachment
            ?? throw new InvalidOperationException("FreeAgent's attachment upload response did not include attachment metadata.");
    }

    public async Task<ContactWire?> GetContactAsync(string contactUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, contactUrl, content: null, cancellationToken);
        // 404 and 400 are both realistic for a caller-supplied contact URL (a deleted contact, or
        // one imported/forged from another company/environment that FreeAgent rejects as malformed)
        // and get the same "not found" treatment here - see docs/coding-standards.md's "Enumerate
        // external failure modes explicitly". Auth failures and transient server errors still
        // propagate via EnsureSuccessAsync below.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;

        await EnsureSuccessAsync(response, "reading a contact", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ContactResponseWire>(SerializerOptions, cancellationToken);
        return body?.Contact
            ?? throw new InvalidOperationException("FreeAgent's contact response did not include a contact.");
    }

    public async Task<IReadOnlyList<ContactWire>> SearchContactsPageAsync(
        int page, int perPage, CancellationToken cancellationToken)
    {
        var url = $"contacts?view=all&sort=name&page={page}&per_page={perPage}";
        using var response = await SendAsync(HttpMethod.Get, url, content: null, cancellationToken);
        await EnsureSuccessAsync(response, "listing contacts", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ContactsResponseWire>(SerializerOptions, cancellationToken);
        return body?.Contacts ?? [];
    }

    public async Task<IReadOnlyList<string>> GetBankAccountUrlsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "bank_accounts", content: null, cancellationToken);
        await EnsureSuccessAsync(response, "listing bank accounts", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BankAccountsResponseWire>(SerializerOptions, cancellationToken);
        return body?.BankAccounts.Select(a => a.Url).OfType<Uri>().Select(u => u.OriginalString).ToList() ?? [];
    }

    private const int ExplanationsPageSize = 100;

    public async Task<IReadOnlyList<BankTransactionExplanationWire>> GetExplanationsAsync(
        string bankAccountUrl, CancellationToken cancellationToken)
    {
        // Paginated exactly like GetBillsPageAsync's caller pages bills - a bank account can
        // carry far more than one page of explanations, and a Guess beyond the first page must
        // still be found, not silently missed and misreported as a generic lock.
        var explanations = new List<BankTransactionExplanationWire>();
        var page = 1;
        while (true)
        {
            var url =
                $"bank_transaction_explanations?bank_account={Uri.EscapeDataString(bankAccountUrl)}" +
                $"&page={page}&per_page={ExplanationsPageSize}";
            using var response = await SendAsync(HttpMethod.Get, url, content: null, cancellationToken);
            await EnsureSuccessAsync(response, "listing bank transaction explanations", cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<BankTransactionExplanationsResponseWire>(SerializerOptions, cancellationToken);
            var pageResults = body?.Explanations ?? [];
            if (pageResults.Count == 0)
                break;

            explanations.AddRange(pageResults);
            if (pageResults.Count < ExplanationsPageSize)
                break;

            page++;
        }

        return explanations;
    }

    public async Task DeleteExplanationAsync(string explanationUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, explanationUrl, content: null, cancellationToken);
        await EnsureSuccessAsync(response, "deleting a bank transaction explanation", cancellationToken);
    }

    public async Task<BankTransactionWire> GetBankTransactionAsync(string bankTransactionUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, bankTransactionUrl, content: null, cancellationToken);
        await EnsureSuccessAsync(response, "reading a bank transaction", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<BankTransactionResponseWire>(SerializerOptions, cancellationToken);
        return body?.BankTransaction ?? throw new InvalidOperationException("FreeAgent returned no bank transaction body.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        // url is frequently an absolute resource URL echoed back by FreeAgent itself (a bill's
        // own "url" field, an explanation/transaction url, ...) rather than a path relative to
        // BaseAddress. Resolve against BaseAddress exactly as HttpClient itself will, rather than
        // checking request.RequestUri.IsAbsoluteUri directly - a scheme-relative reference like
        // "//attacker.example/bills/1" is NOT absolute on its own (no scheme), so it would skip
        // an IsAbsoluteUri-gated check, but RFC 3986 URI-combining rules mean HttpClient still
        // resolves it against BaseAddress by replacing the entire host, not just appending a
        // path. Resolving here first closes that gap and holds for every request shape.
        var resolved = request.RequestUri!.IsAbsoluteUri
            ? request.RequestUri
            : new Uri(httpClient.BaseAddress!, request.RequestUri);
        if (!string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, httpClient.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to call FreeAgent host '{resolved.Scheme}://{resolved.Host}': expected 'https://{httpClient.BaseAddress!.Host}'.");
        }

        var token = await tokenProvider.AcquireTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        // Never log the response body: FreeAgent error bodies can echo request parameters,
        // and this boundary must never leak tokens/secrets into logs or exceptions.
        throw new InvalidOperationException(
            $"FreeAgent request failed while {action}: {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    /// <summary>
    /// Looks for the proven locked-field substrings ("cached_total_value" /
    /// "bill_items.total_value") in a 422 body, alongside the "locked" wording FreeAgent's
    /// proven locked response always carries (e.g. "is locked and cannot be changed") - the
    /// field name alone is not enough, since the same field name is also used for ordinary
    /// validation errors (e.g. "is not a number") that must not be misreported as a lock.
    /// Returns a short, redacted detail string - never the raw body, which could contain other
    /// request context - or null if the body carries no locked-field signal.
    /// </summary>
    private static string? ExtractLockedFieldDetail(string errorBody)
    {
        if (!errorBody.Contains("locked", StringComparison.OrdinalIgnoreCase))
            return null;
        if (errorBody.Contains("cached_total_value", StringComparison.OrdinalIgnoreCase))
            return "cached_total_value is locked";
        if (errorBody.Contains("bill_items.total_value", StringComparison.OrdinalIgnoreCase))
            return "bill_items.total_value is locked";
        return null;
    }
}
