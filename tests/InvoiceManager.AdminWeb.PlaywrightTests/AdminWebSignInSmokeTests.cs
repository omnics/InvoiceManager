using Microsoft.Playwright;

namespace InvoiceManager.AdminWeb.PlaywrightTests;

[Collection("AdminWebAppHost")]
[Trait("Category", "Integration")]
public sealed class AdminWebSignInSmokeTests(AdminWebAppHostFixture appHost)
{
    [Fact]
    public async Task HomePage_WithSavedStorageState_RendersPastSignIn()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        await page.GotoAsync(appHost.AdminWebUrl.ToString());

        Assert.DoesNotContain("/signin-oidc", page.Url, StringComparison.OrdinalIgnoreCase);
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Invoice processing");
    }

    [Fact]
    public async Task AuthorizationPage_WithSavedStorageState_RendersAuthorizationStatus()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/Authorization").ToString());

        Assert.DoesNotContain("/signin-oidc", page.Url, StringComparison.OrdinalIgnoreCase);
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Authorization");
    }
}
