using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     ManagerUiViews HTML encoding for titles, CSRF, and login errors.
/// </summary>
[TestClass]
public class ManagerUiViewsEncodingBehaviorTests
{
    [TestMethod]
    public void Enc_EscapesHtmlSpecialCharacters()
    {
        Assert.AreEqual("", ManagerUiViews.Enc(null));
        Assert.AreEqual("&lt;script&gt;&amp;&quot;&#39;", ManagerUiViews.Enc("<script>&\"'"));
    }

    [TestMethod]
    public void Login_AndPage_EncodeErrorTitleRoleAndCsrf()
    {
        var login = ManagerUiViews.Login("<b>bad</b>");
        StringAssert.Contains(login, "&lt;b&gt;bad&lt;/b&gt;");
        Assert.IsFalse(login.Contains("<b>bad</b>", StringComparison.Ordinal));

        var page = ManagerUiViews.Page(
            ManagerUiView.Fleets,
            role: "editor\"x",
            csrfToken: "tok&1",
            title: "T<script>",
            body: "<p>ok</p>");
        StringAssert.Contains(page, "Titanium Manager - T&lt;script&gt;");
        StringAssert.Contains(page, "data-view=\"fleets\"");
        StringAssert.Contains(page, "data-role=\"editor&quot;x\"");
        StringAssert.Contains(page, "data-csrf=\"tok&amp;1\"");
        StringAssert.Contains(page, "<p>ok</p>");
    }
}
