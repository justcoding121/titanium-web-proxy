using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.E2E.Tests.UiManager;

/// <summary>Playwright against an in-process titanium-manager HTML UI.</summary>
[TestClass]
public class ManagerUiPlaywrightTests : PageTest
{
    private ManagerUiPlaywrightHost? _host;

    [TestInitialize]
    public async Task StartManagerAsync()
    {
        _host = await ManagerUiPlaywrightHost.StartAsync();
    }

    [TestCleanup]
    public async Task StopManagerAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI-Manager")]
    public async Task SignIn_CreateFleet_EditPublish_ShowsRolloutAndAudit()
    {
        var prefix = _host!.BaseUrl;

        await Page.GotoAsync(prefix + "login");
        await Expect(Page.GetByTestId("manager-login")).ToBeVisibleAsync();
        await Page.GetByTestId("login-token").FillAsync(_host.AdminToken);
        await Page.GetByTestId("login-submit").ClickAsync();

        await Expect(Page.GetByTestId("manager-ui")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("whoami")).ToContainTextAsync("admin");

        var cookie = (await Context.CookiesAsync()).Single(c => c.Name == "titanium_manager_session");
        Assert.IsTrue(cookie.HttpOnly);
        Assert.AreEqual(SameSiteAttribute.Strict, cookie.SameSite);

        await Page.GetByTestId("fleet-name").FillAsync("pw-fleet");
        await Page.GetByTestId("btn-create-fleet").ClickAsync();
        await Expect(Page.GetByTestId("fleet-detail")).ToBeVisibleAsync();

        await Page.GetByTestId("link-editor").ClickAsync();
        await Expect(Page.GetByTestId("editor")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("draft-yaml")).ToBeVisibleAsync();

        await Page.GetByTestId("cluster-id").FillAsync("backend");
        await Page.GetByTestId("btn-upsert-cluster").ClickAsync();
        await Expect(Page.GetByTestId("cluster-item-backend")).ToBeVisibleAsync();

        await Page.GetByTestId("route-id").FillAsync("web");
        await Page.GetByTestId("route-path").FillAsync("/");
        await Page.GetByTestId("route-cluster").FillAsync("backend");
        await Page.GetByTestId("btn-upsert-route").ClickAsync();
        await Expect(Page.GetByTestId("route-item-web")).ToBeVisibleAsync();

        await Page.GetByTestId("btn-validate").ClickAsync();
        await Expect(Page.GetByTestId("flash")).ToContainTextAsync("Validation", new() { Timeout = 10_000 });

        await Page.GetByTestId("btn-publish").ClickAsync();
        await Expect(Page.GetByTestId("rollout-detail")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(Page.GetByTestId("btn-rollback-rollout")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("btn-pause-rollout")).ToBeVisibleAsync();

        await Page.GetByTestId("nav-audit").ClickAsync();
        await Expect(Page.GetByTestId("audit")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid='audit-action']").Filter(new() { HasText = "revision.publish" }).First)
            .ToBeVisibleAsync();
    }

    [TestMethod]
    [TestCategory("E2E-UI-Manager")]
    public async Task WrongToken_ShowsLoginError()
    {
        await Page.GotoAsync(_host!.BaseUrl + "login");
        await Page.GetByTestId("login-token").FillAsync("wrong-token");
        await Page.GetByTestId("login-submit").ClickAsync();
        await Expect(Page.GetByTestId("login-error")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("login-form")).ToBeVisibleAsync();
    }
}
