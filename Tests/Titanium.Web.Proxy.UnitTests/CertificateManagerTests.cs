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
        public async Task Simple_Create_Certificate_Stress_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(new Lazy<Action<Exception>>(() => (e => { })).Value);

            mgr.ClearIdleCertificates(1);

            for (int i = 0; i < 1000; i++)
            {
                foreach (var host in hostNames)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await Task.Delay(random.Next(0, 10) * 1000);

                        //get the connection
                        var certificate = mgr.CreateCertificate(host, false);

                        Assert.IsNotNull(certificate);
                    }));
                }
            }

            await Task.WhenAll(tasks.ToArray());

            mgr.StopClearIdleCertificates();
        }
    }
}
