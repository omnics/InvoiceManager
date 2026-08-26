using Microsoft.Playwright;

namespace InvoiceManager.AdminWeb.PlaywrightTests;

[Collection("AdminWebAppHost")]
[Trait("Category", "Integration")]
public sealed class AdminWebNavigationTests(AdminWebAppHostFixture appHost)
{
    [Fact]
    public async Task NavMenu_OpensAndClosesViaHamburgerToggle()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        await page.GotoAsync(appHost.AdminWebUrl.ToString());
        var menu = page.Locator("#nav-menu");
        var toggle = page.Locator("#nav-toggle");

        await Assertions.Expect(menu).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));

        await toggle.ClickAsync();
        await Assertions.Expect(menu).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));

        await toggle.ClickAsync();
        await Assertions.Expect(menu).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));
    }

    [Fact]
    public async Task NavMenu_ClosesOnEscapeKey()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        await page.GotoAsync(appHost.AdminWebUrl.ToString());
        var menu = page.Locator("#nav-menu");

        await page.Locator("#nav-toggle").ClickAsync();
        await Assertions.Expect(menu).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(menu).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));
    }

    [Fact]
    public async Task NavMenu_ClosesOnBackdropClick()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        await page.GotoAsync(appHost.AdminWebUrl.ToString());
        var menu = page.Locator("#nav-menu");

        await page.Locator("#nav-toggle").ClickAsync();
        await Assertions.Expect(menu).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));

        await page.Locator("#nav-backdrop").ClickAsync(new LocatorClickOptions { Force = true });
        await Assertions.Expect(menu).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));
    }

    [Theory]
    [InlineData("Home", "Home")]
    [InlineData("Reauthorization", "Authorization")]
    [InlineData("Invoice configurations", "Invoice configurations")]
    [InlineData("Health status", "Health status")]
    public async Task NavMenu_NavigatesToExpectedPage(string linkName, string expectedHeading)
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        await page.GotoAsync(appHost.AdminWebUrl.ToString());

        await page.Locator("#nav-toggle").ClickAsync();
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = linkName }).ClickAsync();

        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync(expectedHeading);
        await Assertions.Expect(page.Locator("#nav-menu")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("open"));
    }

    [Fact]
    public async Task ServiceStatusPage_RendersHealthCheckResults()
    {
        using var playwright = await Playwright.CreateAsync();
        var (browser, page) = await AdminWebSignedInPageFactory.CreateAsync(playwright);
        await using var _ = browser;

        await page.GotoAsync(new Uri(appHost.AdminWebUrl, "/ServiceStatus").ToString());

        // cosmos, functions, freeagent-authorization, microsoft-authorization.
        var panels = page.Locator(".status-panel");
        await Assertions.Expect(panels).ToHaveCountAsync(4);

        var statusValues = page.Locator(".status-panel .status-value");
        var count = await statusValues.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var text = await statusValues.Nth(i).TextContentAsync();
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }
}
