using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class RunCommandTests
{
    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForStaticFiles()
    {
        var cfg = new TwpConfig
        {
            StaticFiles = new StaticFilesConfig { Root = "www" },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_False_ForPlainForwardHost()
    {
        var cfg = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig { Port = 8080, ForwardHost = "127.0.0.1", ForwardPort = 80 },
            ],
        };
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForTransforms()
    {
        var cfg = new TwpConfig
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Transforms = [new TransformConfig { Kind = "PathRemovePrefix" }],
                },
            ],
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForAcmeDomain()
    {
        var cfg = new TwpConfig
        {
            Certificates = new CertificatesConfig { AcmeDomain = "example.test" },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ListenerConfig_EnableHttp2AndHttp3_FieldsExist()
    {
        var listener = new ListenerConfig
        {
            Port = 8443,
            EnableHttp2 = false,
            EnableHttp3 = true,
        };
        Assert.AreEqual(false, listener.EnableHttp2);
        Assert.IsTrue(listener.EnableHttp3);
    }

    [TestMethod]
    public void BuildPlusOptions_MergesControlPlane()
    {
        var plus = new PlusConfig
        {
            Enabled = true,
            ControlPlane = new ControlPlaneConfig
            {
                Host = "127.0.0.1",
                Port = 9099,
                SharedSecret = "s3cret",
            },
            Options = new Dictionary<string, string> { ["extra"] = "1" },
        };

        var opts = RunCommand.BuildPlusOptions(plus);
        Assert.AreEqual("127.0.0.1", opts["controlPlane.host"]);
        Assert.AreEqual("9099", opts["controlPlane.port"]);
        Assert.AreEqual("s3cret", opts["controlPlane.sharedSecret"]);
        Assert.AreEqual("1", opts["extra"]);
    }
}
