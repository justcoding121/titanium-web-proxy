using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class FingerprintValidationTests
{
    [TestMethod]
    public void FingerprintWithoutTheManagedStackIsAnError()
    {
        var errors = string.Join("; ", TwpConfigValidator.Validate(Config(managedStack: null)));
        StringAssert.Contains(errors, "UseManagedTlsStack");
        StringAssert.Contains(errors, "TITANIUM_MANAGED_STACK");
        StringAssert.Contains(errors, ManagedStackRequiredException.Reason);
    }

    [TestMethod]
    public void AnUnknownFingerprintIsAnError()
    {
        var errors = string.Join("; ", TwpConfigValidator.Validate(Config(managedStack: true, profile: "other")));
        StringAssert.Contains(errors, "unknown");
    }

    [TestMethod]
    public void ChromeWithTheManagedStackIsAccepted()
    {
        var errors = TwpConfigValidator.Validate(Config(managedStack: true));
        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
    }

    private static TwpConfig Config(bool? managedStack, string profile = "chrome") => new()
    {
        Server = new ServerConfig { ManagedStack = managedStack },
        Clusters =
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 443, UseHttps = true }],
                HttpClient = new ClusterHttpClientConfig { Fingerprint = profile },
            },
        ],
    };
}
