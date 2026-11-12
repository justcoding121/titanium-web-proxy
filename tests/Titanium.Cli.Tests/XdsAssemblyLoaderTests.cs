using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Xds;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class XdsAssemblyLoaderTests
{
    [TestMethod]
    public void TryCreateHost_ThrowsWhenXdsAssemblyIsMissingBesideTheCli()
    {
        // In the test host the CLI output directory may or may not contain Titanium.Xds.dll.
        // When it is absent, the loader must name the fix; when present, construction succeeds.
        try
        {
            var host = XdsAssemblyLoader.TryCreateHost(
                new XdsConfig { Server = "127.0.0.1:18000" },
                new TwpConfig(),
                Path.GetTempPath(),
                _ => { });
            Assert.IsNotNull(host);
            if (host is IAsyncDisposable disposable)
            {
                disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (FileNotFoundException ex)
        {
            StringAssert.Contains(ex.Message, "titanium update --plus");
            StringAssert.Contains(ex.FileName ?? string.Empty, "Titanium.Xds.dll");
        }
    }
}
