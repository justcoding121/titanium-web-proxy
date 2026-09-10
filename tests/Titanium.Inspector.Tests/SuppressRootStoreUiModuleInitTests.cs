using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SuppressRootStoreUiModuleInitTests
{
    [TestMethod]
    public void ModuleInit_AreInteractiveRootStoreMutationsSuppressed()
    {
        Assert.IsTrue(
            CertificateManager.AreInteractiveRootStoreMutationsSuppressed,
            "Inspector ModuleInit must suppress interactive Root mutations");
        Assert.AreEqual(
            "1",
            Environment.GetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI"),
            "Inspector unit suites must set TITANIUM_SKIP_ROOT_STORE_UI=1");
    }
}
