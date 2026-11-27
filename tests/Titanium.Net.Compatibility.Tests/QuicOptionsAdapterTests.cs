#pragma warning disable CA1416 // The tests build platform option objects. They do not listen or connect.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Net.Compatibility;

namespace Titanium.Net.Compatibility.Tests;

[TestClass]
public sealed class QuicOptionsAdapterTests
{
    [TestMethod]
    public void AnIpEndpointCopiesTheHostTheProtocolsAndTheTimeout()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 443);
        var local = new IPEndPoint(IPAddress.Loopback, 0);
        var authentication = new SslClientAuthenticationOptions
        {
            TargetHost = "origin.example",
            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
        };
        var source = new QuicClientConnectionOptions
        {
            RemoteEndPoint = remote,
            LocalEndPoint = local,
            ClientAuthenticationOptions = authentication,
            HandshakeTimeout = TimeSpan.FromSeconds(3),
        };

        var managed = QuicClientOptionsAdapter.ToManaged(source);

        Assert.AreSame(remote, managed.RemoteEndPoint);
        Assert.AreSame(local, managed.LocalEndPoint);
        Assert.AreSame(authentication, managed.ClientAuthenticationOptions);
        Assert.AreEqual(TimeSpan.FromSeconds(3), managed.HandshakeTimeout);
        Assert.AreEqual(0, QuicClientOptionsAdapter.UnsupportedSettings(source).Count);
    }

    [TestMethod]
    public void AHostNameIsRefusedUntilTheCallerResolvesIt()
    {
        var source = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint("origin.example", 443),
        };

        IReadOnlyList<string> unsupported = QuicClientOptionsAdapter.UnsupportedSettings(source);
        Assert.AreEqual(1, unsupported.Count);
        Assert.AreEqual(nameof(QuicClientConnectionOptions.RemoteEndPoint), unsupported[0]);

        var error = Assert.ThrowsExactly<ArgumentException>(() => QuicClientOptionsAdapter.ToManaged(source));
        StringAssert.Contains(error.Message, nameof(QuicClientConnectionOptions.RemoteEndPoint));
    }

    [TestMethod]
    public void KeepaliveIdleLimitsAndStreamErrorsAreRefused()
    {
        var source = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            IdleTimeout = TimeSpan.FromSeconds(30),
            MaxInboundBidirectionalStreams = 4,
            MaxInboundUnidirectionalStreams = 3,
            DefaultStreamErrorCode = 0x10c,
        };

        IReadOnlyList<string> unsupported = QuicClientOptionsAdapter.UnsupportedSettings(source);

        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(QuicClientConnectionOptions.KeepAliveInterval),
                nameof(QuicClientConnectionOptions.IdleTimeout),
                nameof(QuicClientConnectionOptions.MaxInboundBidirectionalStreams),
                nameof(QuicClientConnectionOptions.MaxInboundUnidirectionalStreams),
                nameof(QuicClientConnectionOptions.DefaultStreamErrorCode),
            },
            (System.Collections.ICollection)unsupported);
        Assert.ThrowsExactly<ArgumentException>(() => QuicClientOptionsAdapter.ToManaged(source));
    }

    [TestMethod]
    public void ANullClientOptionsThrows()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => QuicClientOptionsAdapter.ToManaged(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => QuicClientOptionsAdapter.UnsupportedSettings(null!));
    }

    [TestMethod]
    public void AListenerCopiesTheEndpointAndKeepsTheManagedHandshakeTimeout()
    {
        var listen = new IPEndPoint(IPAddress.Loopback, 443);
        var source = new QuicListenerOptions
        {
            ListenEndPoint = listen,
        };

        var managed = QuicListenerOptionsAdapter.ToManaged(source, static (_, _, _) =>
            throw new InvalidOperationException("The callback is stored, not called."));

        Assert.AreSame(listen, managed.ListenEndPoint);
        Assert.AreEqual(TimeSpan.FromSeconds(10), managed.HandshakeTimeout);
        Assert.IsNotNull(managed.ConnectionOptionsCallback);
        Assert.AreEqual(0, QuicListenerOptionsAdapter.UnsupportedSettings(source).Count);
    }

    [TestMethod]
    public void AListenerBacklogOtherThanThePlatformDefaultIsRefused()
    {
        var defaults = new QuicListenerOptions();
        var source = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
            ListenBacklog = defaults.ListenBacklog + 1,
        };

        IReadOnlyList<string> unsupported = QuicListenerOptionsAdapter.UnsupportedSettings(source);
        Assert.AreEqual(nameof(QuicListenerOptions.ListenBacklog), unsupported[0]);
    }

    [TestMethod]
    public void ListenerApplicationProtocolsAreRefused()
    {
        var source = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
        };

        IReadOnlyList<string> unsupported = QuicListenerOptionsAdapter.UnsupportedSettings(source);
        Assert.AreEqual(nameof(QuicListenerOptions.ApplicationProtocols), unsupported[0]);
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuicListenerOptionsAdapter.ToManaged(source, static (_, _, _) =>
                throw new InvalidOperationException("The callback is stored, not called.")));
    }

    [TestMethod]
    public void ANullListenerOptionsThrows()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            QuicListenerOptionsAdapter.ToManaged(null!, static (_, _, _) =>
                throw new InvalidOperationException("The callback is stored, not called.")));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            QuicListenerOptionsAdapter.ToManaged(new QuicListenerOptions(), null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => QuicListenerOptionsAdapter.UnsupportedSettings(null!));
    }
}
#pragma warning restore CA1416
