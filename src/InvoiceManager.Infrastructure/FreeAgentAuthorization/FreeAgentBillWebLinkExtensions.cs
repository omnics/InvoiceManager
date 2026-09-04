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
        /// <see cref="FreeAgentOptions.Subdomain"/> isn't configured - never a guess at a
        /// link that might not resolve.
        /// </summary>
        public Option<Uri> WebUrl(FreeAgentOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Subdomain))
                return Option.None;

            var id = bill.Url.Segments[^1].TrimEnd('/');
            return new Uri(FreeAgentHosts.AppBaseUri(options.Environment, options.Subdomain), $"bills/{id}");
        }
    }
}
