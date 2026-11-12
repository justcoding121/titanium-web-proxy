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
public class CertificateManagerCacheSeamWave42FatGapTests
{


    [TestMethod]
    public void CertificateManager_CacheFindUninstallAndSslContextSeams()
    {
        var isTruthy = typeof(CertificateManager).GetMethod("IsTruthyEnv", Wave42FatGapTestSupport.PrivateStatic)!;
        var previous = Environment.GetEnvironmentVariable("TWP_SONAR_TRUTHY_COV");
        try
        {
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", "yes");
            Assert.IsTrue((bool)isTruthy.Invoke(null, ["TWP_SONAR_TRUTHY_COV"])!);
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", "0");
            Assert.IsFalse((bool)isTruthy.Invoke(null, ["TWP_SONAR_TRUTHY_COV"])!);
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", "true");
            Assert.IsTrue((bool)isTruthy.Invoke(null, ["TWP_SONAR_TRUTHY_COV"])!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TWP_SONAR_TRUTHY_COV", previous);
        }

        var find = typeof(CertificateManager).GetMethod("FindCertificates", Wave42FatGapTestSupport.PrivateStatic)!;
        var empty = (X509Certificate2Collection)find.Invoke(null,
            [StoreName.Root, StoreLocation.CurrentUser, "ffffffffffffffffffffffffffffffffffffffff"])!;
        Assert.AreEqual(0, empty.Count);

        var noMax = typeof(CertificateManager).GetMethod("NoMaxCacheEntries", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.IsNull(noMax.Invoke(null, null));

        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = true,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = mgr.CreateCertificate("cache-seam.example", false);
        Assert.IsNotNull(leaf);

        var cacheField = typeof(CertificateManager).GetField("cachedCertificates", Wave42FatGapTestSupport.PrivateInstance)!;
        var cache = (System.Collections.IDictionary)cacheField.GetValue(mgr)!;
        var cachedType = typeof(CachedCertificate);
        var cached = Activator.CreateInstance(cachedType, leaf)!;
        cachedType.GetProperty("LastAccess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(cached, DateTime.UtcNow);
        cache["cache-seam.example"] = cached;

        var tryGet = typeof(CertificateManager).GetMethod("TryGetValidCachedCertificate", Wave42FatGapTestSupport.PrivateInstance)!;
        var getArgs = new object?[] { "cache-seam.example", null };
        Assert.IsTrue((bool)tryGet.Invoke(mgr, getArgs)!);
        Assert.IsNotNull(getArgs[1]);
        getArgs = ["missing.example", null];
        Assert.IsFalse((bool)tryGet.Invoke(mgr, getArgs)!);

        var loadDisk = typeof(CertificateManager).GetMethod("TryLoadFakeCertificateFromDisk", Wave42FatGapTestSupport.PrivateInstance)!;
        Assert.IsNull(loadDisk.Invoke(mgr, ["no-such-host.example"]));

        var uninstall = typeof(CertificateManager).GetMethod("UninstallCertificate", Wave42FatGapTestSupport.PrivateInstance)!;
        uninstall.Invoke(mgr, [StoreName.My, StoreLocation.CurrentUser, null]);

        var stage = typeof(CertificateManager).GetMethod("StageIntermediateForOsChainBuild", Wave42FatGapTestSupport.PrivateStatic)!;
        stage.Invoke(null, [mgr.RootCertificate!]);

        var buildCtx = typeof(CertificateManager).GetMethod("BuildSslCertificateContext", Wave42FatGapTestSupport.PrivateInstance)!;
        var ctx = buildCtx.Invoke(mgr, [leaf]);
        Assert.IsNotNull(ctx);

        var rootInstalled = typeof(CertificateManager).GetMethod("RootCertificateInstalled", Wave42FatGapTestSupport.PrivateInstance)!;
        _ = (bool)rootInstalled.Invoke(mgr, [StoreLocation.CurrentUser])!;

        var orphan = typeof(CertificateManager).GetMethod("RemoveOrphanedSameCommonNameCertificates", Wave42FatGapTestSupport.PrivateInstance)!;
        orphan.Invoke(mgr, [StoreLocation.CurrentUser, true]);
    }
}
#pragma warning restore TWP001
