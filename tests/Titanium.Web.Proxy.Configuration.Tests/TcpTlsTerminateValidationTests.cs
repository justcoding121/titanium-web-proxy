using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class TcpTlsTerminateValidationTests
{
    [TestMethod]
    public void TerminateTls_RequiresPassthroughAndCertificate()
    {
        var config = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig
                {
                    Port = 8443,
                    Type = "transparent",
                    TerminateTls = true,
                    Passthrough = false,
                },
            ],
        };
        var errors = string.Join("; ", TwpConfigValidator.Validate(config));
        StringAssert.Contains(errors, "terminateTls requires passthrough: true");
        StringAssert.Contains(errors, "terminateTls needs a certificate");
    }
}
