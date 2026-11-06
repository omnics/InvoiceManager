namespace InvoiceManager.Integrations.FreeAgent.IntegrationTests;

/// <summary>
/// Opt-in: exercises the real FreeAgent sandbox API end to end (Key Vault refresh token -&gt;
/// FreeAgentTokenProvider -&gt; FreeAgentApiClient -&gt; FreeAgentContactDirectory), the way a
/// StubHttpMessageHandler-based unit test never can. This is deliberately the lightest-weight
/// integration test for FreeAgent in this repo: unlike the Microsoft/Graph equivalent
/// (ConfigurationEditLazyBillingTests.cs et al. in InvoiceManager.AdminWeb.PlaywrightTests),
/// FreeAgent's OAuth is a plain rotating refresh token already sitting in Key Vault once an
/// administrator has completed authorization once via AdminWeb - no interactive browser/OIDC
/// flow is needed to *use* it, only to originally capture it - so this test talks to
/// FreeAgentApiClient directly rather than booting the whole AppHost through a browser.
/// See <see cref="FreeAgentSandbox"/> for the prerequisites.
///
/// Excluded from CI by the same `Category=Integration` filter as every other opt-in sandbox test
/// in this repo (see AGENTS.md) - treat a green run as informative, not authoritative.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FreeAgentContactDirectorySandboxTests
{
    [Fact]
    public async Task SearchAsync_SucceedsAgainstTheRealSandboxApi()
    {
        using var sandbox = await FreeAgentSandbox.ConnectAsync();
        var directory = new FreeAgentContactDirectory(sandbox.ApiClient);

        // Not asserting on specific contact data - the sandbox company's contents can change
        // freely and aren't this test's concern. What matters is that authentication succeeds and
        // FreeAgent accepts the request at all: a StubHttpMessageHandler-based unit test can
        // never catch a real API rejection like the missing User-Agent header FreeAgent requires
        // on every request, which broke every live FreeAgent call until manual testing surfaced
        // it - this test exists so a regression like that fails loudly for anyone who runs it
        // locally with real credentials, rather than only being found by hand again.
        var results = await directory.SearchAsync("a");

        Assert.NotNull(results);
    }
}
