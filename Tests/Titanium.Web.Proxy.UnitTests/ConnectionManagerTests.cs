using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class ConnectionManagerTests
    {
        private readonly static string[] hostNames
            = new string[] { "facebook.com", "youtube.com", "google.com",
                                "bing.com", "yahoo.com"};

        private readonly Random random = new Random();

        [TestMethod]
        public async Task Simple_Create_Connection_Stress_Test()
        {
            var tasks = new List<Task>();

            var mgr = new TcpConnectionManager();

            mgr.ClearIdleConnections(1);

            for (int i = 0; i < 1000; i++)
            {
                foreach (var host in hostNames)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await Task.Delay(random.Next(0, 10) * 1000);

                             //get the connection
                             var httpConnection = await mgr.GetClient(host, 80, false, new Version(1, 1), null, null,
                            8096, System.Security.Authentication.SslProtocols.Default,
                                120, null, null);

                             //simulate a work with the connection
                             await Task.Delay(random.Next(0, 10) * 1000);

                             //print total number of connections on cache
                             Debug.WriteLine(mgr.connectionCache.Count);

                        Assert.IsNotNull(httpConnection);
                        Assert.IsNotNull(httpConnection.TcpClient);
                        Assert.IsTrue(httpConnection.TcpClient.Connected);

                             //release
                             await mgr.ReleaseClient(httpConnection);
                    }));

                    tasks.Add(Task.Run(async () =>
                    {
                        await Task.Delay(random.Next(0, 10) * 1000);
                             //get the connection
                             var httpsConnection = await mgr.GetClient(host, 443, true, new Version(1, 1), null, null,
                            8096, System.Security.Authentication.SslProtocols.Default,
                                120, null, null);

                             //simulate a work with the connection
                             await Task.Delay(random.Next(0, 10) * 1000);

                             //print total number of connections on cache
                             Debug.WriteLine(mgr.connectionCache.Count);

                        Assert.IsNotNull(httpsConnection);
                        Assert.IsNotNull(httpsConnection.TcpClient);
                        Assert.IsTrue(httpsConnection.TcpClient.Connected);

                             //release
                             await mgr.ReleaseClient(httpsConnection);
                    }));
                }
            }

            await Task.WhenAll(tasks.ToArray());

            mgr.StopClearIdleConnections();

        }
    }
}
