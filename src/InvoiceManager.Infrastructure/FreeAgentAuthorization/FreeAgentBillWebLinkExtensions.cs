using InvoiceManager.Core;
using InvoiceManager.Core.Integrations.FreeAgent;

namespace InvoiceManager.Infrastructure.FreeAgentAuthorization;

/// <summary>Builds a browsable FreeAgent web-app link from an API-resource bill identity.</summary>
public static class FreeAgentBillWebLinkExtensions
{
    extension(FreeAgentBillIdentity bill)
    {
        /// <summary>
        /// The bill's URL in FreeAgent's own browsable web app, or <see cref="None"/> if
        /// <paramref name="subdomain"/> isn't known (FreeAgent has never been authorized, or was
        /// authorized before its subdomain was captured) - never a guess at a link that might
        /// not resolve.
        /// </summary>
        public Option<Uri> WebUrl(FreeAgentEnvironment environment, string? subdomain)
        {
            if (string.IsNullOrWhiteSpace(subdomain))
                return Option.None;

            var id = bill.Url.Segments[^1].TrimEnd('/');
            return new Uri(FreeAgentHosts.AppBaseUri(environment, subdomain), $"bills/{id}");
        }
    }
}
