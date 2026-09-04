using Microsoft.Extensions.Options;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

public sealed class FreeAgentAuthorizationOptions
{
    public const string SectionName = "FreeAgentAuthorization";

    public const string DefaultRefreshTokenSecretName = "FreeAgentAuthorization--RefreshToken";

    public const string DefaultSubdomainSecretName = "FreeAgentAuthorization--Subdomain";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string RefreshTokenSecretName { get; set; } = DefaultRefreshTokenSecretName;

    /// <summary>
    /// Where the authorized FreeAgent account's web-app subdomain is stored - captured from
    /// FreeAgent's own company resource right after authorization (see
    /// <see cref="FreeAgentCompanyLookup"/>), not configured, since it depends on which account
    /// gets authorized rather than being known at deployment time.
    /// </summary>
    public string SubdomainSecretName { get; set; } = DefaultSubdomainSecretName;

    public bool HasClientConfiguration =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class FreeAgentAuthorizationOptionsValidator : IValidateOptions<FreeAgentAuthorizationOptions>
{
    public ValidateOptionsResult Validate(string? name, FreeAgentAuthorizationOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add("FreeAgentAuthorization:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            failures.Add("FreeAgentAuthorization:ClientSecret is required.");
        }

        if (string.IsNullOrWhiteSpace(options.RefreshTokenSecretName))
        {
            failures.Add("FreeAgentAuthorization:RefreshTokenSecretName is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SubdomainSecretName))
        {
            failures.Add("FreeAgentAuthorization:SubdomainSecretName is required.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
