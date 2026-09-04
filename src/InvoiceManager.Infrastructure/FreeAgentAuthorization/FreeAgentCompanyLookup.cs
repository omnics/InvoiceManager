using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

/// <summary>
/// Looks up the FreeAgent company (account) a freshly-issued OAuth access token belongs to -
/// used right after the authorization-code exchange, before any refresh token is stored, so
/// this is a small standalone HTTP call rather than going through
/// <see cref="IFreeAgentTokenProvider"/>/the internal FreeAgent API client (both of which
/// assume a durable refresh token already exists).
/// </summary>
public interface IFreeAgentCompanyLookup
{
    Task<FreeAgentCompany> GetCompanyAsync(
        string accessToken, FreeAgentEnvironment environment, CancellationToken cancellationToken = default);
}

public sealed class FreeAgentCompanyLookup(HttpClient httpClient) : IFreeAgentCompanyLookup
{
    // FreeAgent's company response is snake_case (subdomain, company_start_date, ...) - same
    // policy as FreeAgentApiClient.SerializerOptions/FreeAgentTokenProvider.SerializerOptions.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<FreeAgentCompany> GetCompanyAsync(
        string accessToken, FreeAgentEnvironment environment, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(FreeAgentHosts.ApiBaseUri(environment), "company"))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Never include the response body: it can echo request parameters.
            throw new InvalidOperationException(
                $"FreeAgent company lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var companyResponse = await response.Content.ReadFromJsonAsync<CompanyResponseWire>(SerializerOptions, cancellationToken);
        var subdomain = companyResponse?.Company?.Subdomain;
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            throw new InvalidOperationException("FreeAgent's company response did not include a subdomain.");
        }

        return new FreeAgentCompany(subdomain);
    }

    private sealed class CompanyResponseWire
    {
        public CompanyWire? Company { get; init; }
    }

    private sealed class CompanyWire
    {
        public string? Subdomain { get; init; }
    }
}
