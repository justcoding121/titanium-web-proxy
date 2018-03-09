using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class CertificateManagerTests
    {
        private static readonly string[] hostNames
            = { "facebook.com", "youtube.com", "google.com", "bing.com", "yahoo.com" };

        private readonly Random random = new Random();

        [TestMethod]
        public async Task Simple_Create_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(new Lazy<Action<Exception>>(() => (e => { })).Value);

            mgr.ClearIdleCertificates();

            foreach (string host in hostNames)
            {
                tasks.Add(Task.Run(async () =>
                {

                    //get the connection
                    var certificate = await mgr.CreateCertificateAsync(host);

                    Assert.IsNotNull(certificate);
                }));
            }

            await Task.WhenAll(tasks.ToArray());

            mgr.StopClearIdleCertificates();
        }

        //uncomment this to compare WinCert maker performance with BC (BC takes more time for same test above)
        //cannot run this test in build server since trusting the certificate won't happen successfully
        //[TestMethod]
        public async Task Simple_Create_Win_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(new Lazy<Action<Exception>>(() => (e => { })).Value);
            mgr.CreateRootCertificate(true);
            mgr.TrustRootCertificate();
            mgr.ClearIdleCertificates();
            mgr.CertificateEngine = CertificateEngine.DefaultWindows;

            foreach (string host in hostNames)
            {
                tasks.Add(Task.Run(async () =>
                {
                    //get the connection
                    var certificate = await mgr.CreateCertificateAsync(host);

                    Assert.IsNotNull(certificate);
                }));
            }

            await Task.WhenAll(tasks.ToArray());

            mgr.StopClearIdleCertificates();
        }
    }
}
