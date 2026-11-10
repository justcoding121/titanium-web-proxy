#pragma warning disable TWP001
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2OriginConnectionPoolWave42FatGapTests
{


    [TestMethod]
    public async Task Pool_OfferRentInvalidateAndDrain_CoverCapacityAndUnusableBranches()
    {
        using var proxy = new ProxyServer(false, false, false);
        var pool = proxy.Http2OriginConnectionPool;
        const string key = "pool-coverage";

        try
        {
            var first = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
            pool.Offer(key, first);

            var opened = 0;
            var rented = await pool.RentAsync(key, _ =>
            {
                opened++;
                return Task.FromResult(first);
            }, CancellationToken.None);
            Assert.AreSame(first, rented);
            Assert.AreEqual(0, opened);

            pool.Invalidate(key, first);
            first.Touch();
            first.AcquireLease();
            first.ReleaseLease();
            Assert.IsTrue(first.SoftStreamCapacity >= 1);
            Assert.IsFalse(first.IsNearStreamIdExhaustion);
            Assert.IsTrue(first.LastUsedUtc <= DateTime.UtcNow);

            var created = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
            var fromOpen = await pool.RentAsync(key, _ => Task.FromResult(created), CancellationToken.None);
            Assert.AreSame(created, fromOpen);

            var exhausted = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
            typeof(Http2OriginConnection).GetField("lastStreamId", Wave42FatGapTestSupport.PrivateInstance)!
                .SetValue(exhausted, int.MaxValue - 1);
            pool.Offer(key, exhausted);
            Assert.IsTrue(exhausted.IsNearStreamIdExhaustion);

            var retired = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
            retired.Retire();
            pool.Offer(key, retired);

            var max = ProxyResourceLimits.Default.MaxOriginHttp2ConnectionsPerAuthority;
            for (var i = 0; i < max + 1; i++)
                pool.Offer("at-capacity", await Wave42FatGapTestSupport.CreateShellAsync(proxy));

            Assert.IsFalse(string.IsNullOrEmpty(Http2OriginConnectionPool.DiagPickStats.FormatSummary()));
            Assert.IsFalse(Http2OriginConnectionPool.DiagPickStats.IsEnabled);
        }
        finally
        {
            await pool.DrainAsync();
            await pool.DrainAsync();
        }
    }
}
#pragma warning restore TWP001
