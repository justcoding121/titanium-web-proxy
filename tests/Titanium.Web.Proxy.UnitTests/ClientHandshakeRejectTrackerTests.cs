using System;
using System.IO;
using System.Security.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression-2026-10-07: a client that pins its certificate (or does not trust the proxy root) aborted
///     the MITM handshake for the same host 388 times in minutes. The tracker trips after N consecutive
///     aborts with no success in between so the proxy can stop decrypting that host.
/// </summary>
[TestClass]
public class ClientHandshakeRejectTrackerTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void ConsecutiveFailures_TripExactlyAtThreshold()
    {
        var t = new ClientHandshakeRejectTracker { Threshold = 3 };
        var now = DateTime.UtcNow;
        Assert.IsFalse(t.RecordFailure("api2.cursor.sh", now));
        Assert.IsFalse(t.RecordFailure("api2.cursor.sh", now.AddSeconds(1)));
        Assert.IsTrue(t.RecordFailure("api2.cursor.sh", now.AddSeconds(2)));
        // Tripped entries are cleared: the next abort starts a fresh count.
        Assert.IsFalse(t.RecordFailure("api2.cursor.sh", now.AddSeconds(3)));
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void Success_ResetsTheCount()
    {
        var t = new ClientHandshakeRejectTracker { Threshold = 3 };
        var now = DateTime.UtcNow;
        t.RecordFailure("example.com", now);
        t.RecordFailure("example.com", now);
        t.RecordSuccess("example.com");
        Assert.IsFalse(t.RecordFailure("example.com", now));
        Assert.IsFalse(t.RecordFailure("example.com", now));
        Assert.IsTrue(t.RecordFailure("example.com", now));
    }

    [TestMethod]
    public void FailuresOutsideWindow_DoNotAccumulate()
    {
        var t = new ClientHandshakeRejectTracker { Threshold = 3, Window = TimeSpan.FromMinutes(2) };
        var now = DateTime.UtcNow;
        t.RecordFailure("example.com", now);
        t.RecordFailure("example.com", now.AddSeconds(30));
        Assert.IsFalse(t.RecordFailure("example.com", now.AddMinutes(5)));
    }

    [TestMethod]
    public void HostsAreIndependent_AndCaseInsensitive()
    {
        var t = new ClientHandshakeRejectTracker { Threshold = 2 };
        var now = DateTime.UtcNow;
        Assert.IsFalse(t.RecordFailure("a.example.com", now));
        Assert.IsFalse(t.RecordFailure("b.example.com", now));
        Assert.IsTrue(t.RecordFailure("A.Example.COM", now));
        Assert.IsTrue(t.RecordFailure("b.example.com:443", now), "host:port normalizes to the same key");
    }

    [TestMethod]
    public void ThresholdZero_DisablesTracking()
    {
        var t = new ClientHandshakeRejectTracker { Threshold = 0 };
        for (var i = 0; i < 50; i++)
            Assert.IsFalse(t.RecordFailure("example.com", DateTime.UtcNow));
        Assert.AreEqual(0, t.Count);
    }

    [TestMethod]
    public void Memory_IsBounded()
    {
        var t = new ClientHandshakeRejectTracker { Threshold = 100, MaxEntries = 16 };
        var now = DateTime.UtcNow;
        for (var i = 0; i < 1000; i++)
            t.RecordFailure($"h{i}.example.com", now);
        Assert.IsTrue(t.Count <= 16, $"count={t.Count}");
    }

    [TestMethod]
    public void IsClientHandshakeRejection_ClassifiesAbortsNotCancellation()
    {
        Assert.IsTrue(DecryptFailureLearning.IsClientHandshakeRejection(
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
        Assert.IsTrue(DecryptFailureLearning.IsClientHandshakeRejection(
            new AuthenticationException("The remote party closed the transport stream.")));
        Assert.IsTrue(DecryptFailureLearning.IsClientHandshakeRejection(
            new Exception("wrapped", new IOException("eof"))));
        Assert.IsFalse(DecryptFailureLearning.IsClientHandshakeRejection(new OperationCanceledException()));
        Assert.IsFalse(DecryptFailureLearning.IsClientHandshakeRejection(
            new IOException("x", new OperationCanceledException())));
        Assert.IsFalse(DecryptFailureLearning.IsClientHandshakeRejection(new TimeoutException()));
        Assert.IsFalse(DecryptFailureLearning.IsClientHandshakeRejection(new InvalidOperationException()));
        Assert.IsFalse(DecryptFailureLearning.IsClientHandshakeRejection(null));
        Assert.IsTrue(DecryptFailureLearning.IsClientCertificateRejection(
            new IOException("The decryption operation failed, see inner exception.",
                new System.ComponentModel.Win32Exception(-2146893019))));
        Assert.IsFalse(DecryptFailureLearning.IsClientCertificateRejection(
            new IOException("An existing connection was forcibly closed by the remote host.")));
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void ProxyServer_ThresholdReached_ActivatesBypass_AndRaisesChangedOnce()
    {
        using var proxy = new ProxyServer { EnableDecryptFailureBypass = true, ClientHandshakeRejectThreshold = 3 };
        var raised = 0;
        proxy.DecryptFailureBypassChanged += (_, _) => raised++;
        var abort = new IOException("Received an unexpected EOF or 0 bytes from the transport stream.");

        Assert.IsFalse(proxy.TryRecordClientHandshakeReject("api2.cursor.sh", abort));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("api2.cursor.sh"));
        Assert.IsFalse(proxy.TryRecordClientHandshakeReject("api2.cursor.sh", abort));
        Assert.IsTrue(proxy.TryRecordClientHandshakeReject("api2.cursor.sh", abort));

        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("api2.cursor.sh"));
        Assert.AreEqual(1, raised);
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void ProxyServer_SuccessBetweenAborts_PreventsBypass()
    {
        using var proxy = new ProxyServer { EnableDecryptFailureBypass = true, ClientHandshakeRejectThreshold = 3 };
        var abort = new IOException("eof");

        proxy.TryRecordClientHandshakeReject("example.com", abort);
        proxy.TryRecordClientHandshakeReject("example.com", abort);
        proxy.RecordClientHandshakeSuccess("example.com");
        Assert.IsFalse(proxy.TryRecordClientHandshakeReject("example.com", abort));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("example.com"));
    }

    [TestMethod]
    public void ProxyServer_FeatureOff_NeverBypasses()
    {
        using var proxy = new ProxyServer { EnableDecryptFailureBypass = false, ClientHandshakeRejectThreshold = 1 };
        Assert.IsFalse(proxy.TryRecordClientHandshakeReject("example.com", new IOException("eof")));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("example.com"));
    }

    [TestMethod]
    public void ProxyServer_CancellationIsNotARejection()
    {
        using var proxy = new ProxyServer { EnableDecryptFailureBypass = true, ClientHandshakeRejectThreshold = 1 };
        Assert.IsFalse(proxy.TryRecordClientHandshakeReject("example.com", new OperationCanceledException()));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("example.com"));
    }
}
