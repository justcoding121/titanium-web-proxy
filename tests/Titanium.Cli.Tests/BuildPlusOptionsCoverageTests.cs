using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>Plus option flattening for control-plane dashboard fields a process path rarely sets together.</summary>
[TestClass]
public class BuildPlusOptionsCoverageTests
{
    [TestMethod]
    public void BuildPlusOptions_FlattensDashboardOidcListsAndFlags()
    {
        var plus = new PlusConfig
        {
            Enabled = true,
            Options = new Dictionary<string, string> { ["existing"] = "keep" },
            ControlPlane = new ControlPlaneConfig
            {
                Host = "127.0.0.1",
                Port = 9080,
                DashboardPort = 9443,
                SharedSecret = "shared",
                DashboardViewerSecret = "viewer",
                DashboardUsersFile = "users.txt",
                DashboardBreakGlass = true,
                DashboardEditing = false,
                SnapshotValidation = SnapshotValidationMode.Enforce,
                OverlayPath = "overlay.yaml",
                AllowInsecureNonLoopback = false,
                DashboardOidc = new DashboardOidcConfig
                {
                    Issuer = "https://issuer.example",
                    ClientId = "client",
                    ClientSecret = "secret",
                    CookieSecret = "cookie",
                    RedirectUri = "https://app/callback",
                    GroupsClaim = "groups",
                    OperatorGroups = ["ops", " "],
                    ViewerGroups = ["view"],
                    AllowedEmails = ["a@example.com"],
                    AllowedEmailDomains = ["example.com"],
                },
                Tls = new ControlPlaneTlsConfig
                {
                    CertificatePath = "cert.pem",
                    KeyPath = "key.pem",
                },
            },
        };

        var options = RunCommand.BuildPlusOptions(plus);

        Assert.AreEqual("keep", options["existing"]);
        Assert.AreEqual("127.0.0.1", options["controlPlane.host"]);
        Assert.AreEqual("9080", options["controlPlane.port"]);
        Assert.AreEqual("9443", options["controlPlane.dashboardPort"]);
        Assert.AreEqual("shared", options["controlPlane.sharedSecret"]);
        Assert.AreEqual("viewer", options["controlPlane.dashboardViewerSecret"]);
        Assert.AreEqual("users.txt", options["controlPlane.dashboardUsersFile"]);
        Assert.AreEqual("true", options["controlPlane.dashboardBreakGlass"]);
        Assert.AreEqual("false", options["controlPlane.dashboardEditing"]);
        Assert.AreEqual("enforce", options["controlPlane.snapshotValidation"]);
        Assert.AreEqual("overlay.yaml", options["controlPlane.overlayPath"]);
        Assert.AreEqual("https://issuer.example", options["controlPlane.dashboardOidc.issuer"]);
        Assert.AreEqual("client", options["controlPlane.dashboardOidc.clientId"]);
        Assert.AreEqual("secret", options["controlPlane.dashboardOidc.clientSecret"]);
        Assert.AreEqual("cookie", options["controlPlane.dashboardOidc.cookieSecret"]);
        Assert.AreEqual("https://app/callback", options["controlPlane.dashboardOidc.redirectUri"]);
        Assert.AreEqual("groups", options["controlPlane.dashboardOidc.groupsClaim"]);
        Assert.AreEqual("ops", options["controlPlane.dashboardOidc.operatorGroups"]);
        Assert.AreEqual("view", options["controlPlane.dashboardOidc.viewerGroups"]);
        Assert.AreEqual("a@example.com", options["controlPlane.dashboardOidc.allowedEmails"]);
        Assert.AreEqual("example.com", options["controlPlane.dashboardOidc.allowedEmailDomains"]);
        Assert.AreEqual("cert.pem", options["controlPlane.tls.certificatePath"]);
        Assert.AreEqual("key.pem", options["controlPlane.tls.keyPath"]);
        Assert.IsFalse(options.ContainsKey("controlPlane.allowInsecureNonLoopback"));
    }

    [TestMethod]
    public void ResolveLocalZone_ReadsTitaniumZoneEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("TITANIUM_ZONE");
        try
        {
            Environment.SetEnvironmentVariable("TITANIUM_ZONE", "  env-zone ");
            Assert.AreEqual("env-zone", RunCommand.ResolveLocalZone(new ServerConfig { LocalZone = "server" }, null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_ZONE", previous);
        }
    }

    [TestMethod]
    public void ComputeConfigHash_ReturnsEmpty_WhenFileIsMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "twp-missing-" + Guid.NewGuid().ToString("N") + ".yaml");
        Assert.AreEqual(string.Empty, Titanium.Cli.Admin.AdminHost.ComputeConfigHash(missing));
    }
}
