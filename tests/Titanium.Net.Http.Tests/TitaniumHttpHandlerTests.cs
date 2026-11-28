#pragma warning disable CA1416 // QuicStreamType is the BCL name. These tests do not start the platform QUIC stack.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Http3;
using Titanium.Quic;

namespace Titanium.Net.Http.Tests;

[TestClass]
public sealed class TitaniumHttpHandlerTests
{
    [TestMethod]
    public void HttpsDefaultPortStays443()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "https://example.com/a?b=1");
        using HttpRequestMessage inner = TitaniumHttpHandler.CreateInnerRequest(
            request,
            HttpVersion.Version11,
            HttpVersionPolicy.RequestVersionExact);

        Assert.AreEqual("https", request.RequestUri!.Scheme);
        Assert.AreEqual("http", inner.RequestUri!.Scheme);
        Assert.AreEqual(443, inner.RequestUri.Port);
        Assert.AreEqual("/a", inner.RequestUri.AbsolutePath);
        Assert.AreEqual("?b=1", inner.RequestUri.Query);
        Assert.AreEqual("example.com", inner.Headers.Host);
        Assert.IsTrue(inner.Options.TryGetValue(TitaniumHttpHandler.ManagedTlsOption, out bool tls) && tls);
    }

    [TestMethod]
    public void ExplicitHttpsPortIsKept()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "https://example.com:8443/a");
        request.Headers.Host = "example.com:8443";
        using HttpRequestMessage inner = TitaniumHttpHandler.CreateInnerRequest(
            request,
            HttpVersion.Version11,
            HttpVersionPolicy.RequestVersionExact);

        Assert.AreEqual("http", inner.RequestUri!.Scheme);
        Assert.AreEqual(8443, inner.RequestUri.Port);
        Assert.AreEqual("example.com:8443", inner.Headers.Host);
    }

    [TestMethod]
    public void HttpUriIsNotRewritten()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "http://example.com/a");
        using HttpRequestMessage inner = TitaniumHttpHandler.CreateInnerRequest(
            request,
            HttpVersion.Version11,
            HttpVersionPolicy.RequestVersionExact);

        Assert.AreEqual("http", inner.RequestUri!.Scheme);
        Assert.AreEqual(80, inner.RequestUri.Port);
        Assert.IsFalse(inner.Options.TryGetValue(TitaniumHttpHandler.ManagedTlsOption, out _));
    }

    [TestMethod]
    public void AssemblyReferencesTheManagedStackOnly()
    {
        string[] names = typeof(TitaniumHttpHandler).Assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.IsTrue(names.Contains("Titanium.Tls"));
        Assert.IsTrue(names.Contains("Titanium.Quic"));
        Assert.IsTrue(names.Contains("Titanium.Http3"));
        foreach (string name in names)
        {
            Assert.IsFalse(name.Contains("Titanium.Web.Proxy", StringComparison.Ordinal), name);
            Assert.IsFalse(string.Equals(name, "Titanium.Net.Compatibility", StringComparison.Ordinal), name);
        }
    }

    [TestMethod]
    public void PropertiesRoundTripToTheInnerHandler()
    {
        using TitaniumHttpHandler handler = new()
        {
            UseProxy = false,
            MaxConnectionsPerServer = 4,
            AllowAutoRedirect = false,
            MaxAutomaticRedirections = 2,
            AutomaticDecompression = DecompressionMethods.GZip,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(3),
            UseCookies = false,
            EnableAltSvc = true,
            AllowManagedStackUnderFips = true,
            Proxy = null,
        };
        CookieContainer cookies = new();
        handler.CookieContainer = cookies;

        Assert.IsFalse(handler.UseProxy);
        Assert.IsNull(handler.Proxy);
        Assert.AreEqual(4, handler.MaxConnectionsPerServer);
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.AreEqual(2, handler.MaxAutomaticRedirections);
        Assert.AreEqual(DecompressionMethods.GZip, handler.AutomaticDecompression);
        Assert.AreEqual(TimeSpan.FromMinutes(1), handler.PooledConnectionLifetime);
        Assert.AreEqual(TimeSpan.FromSeconds(30), handler.PooledConnectionIdleTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(3), handler.ConnectTimeout);
        Assert.IsFalse(handler.UseCookies);
        Assert.IsTrue(handler.EnableAltSvc);
        Assert.IsTrue(handler.AllowManagedStackUnderFips);
        Assert.AreSame(cookies, handler.CookieContainer);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3AgainstLoopbackReturnsTheBody()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3LeavesGzipEncoded()
    {
        byte[] payload;
        using (MemoryStream encoded = new())
        {
            using (GZipStream gzip = new(encoded, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write("hello"u8);
            }

            payload = encoded.ToArray();
        }

        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.AutomaticDecompression = DecompressionMethods.GZip;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/gzip")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        byte[]? headers = await ReadFrameAsync(inbound, cancel.Token);
        Assert.IsNotNull(headers);
        bool askedForGzip = false;
        foreach ((string name, string value) in QpackDecoder.Decode(headers))
        {
            if (string.Equals(name, "accept-encoding", StringComparison.OrdinalIgnoreCase)
                && value.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                askedForGzip = true;
            }
        }

        while (await ReadFrameAsync(inbound, cancel.Token) is not null)
        {
        }

        byte[] block = QpackEncoder.Encode([(":status", "200"), ("content-encoding", "gzip")]);
        await WriteFrameAsync(inbound, Http3FrameType.Headers, block, cancel.Token);
        await WriteFrameAsync(inbound, Http3FrameType.Data, payload, cancel.Token);
        await inbound.CompleteWritesAsync(cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(body.AsSpan().SequenceEqual(payload));
        Assert.IsFalse(askedForGzip);
        Assert.AreEqual(3, response.Version.Major);
        Assert.IsTrue(response.Content.Headers.ContentEncoding.Contains("gzip"));
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3DirectRequestLeavesTheCookieContainerUnchanged()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        Uri uri = new("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        CookieContainer cookies = new();
        cookies.Add(uri, new Cookie("session", "one"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.CookieContainer = cookies;
        handler.UseCookies = true;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        (string path, string cookie) = await ReadPathAndCookieAsync(inbound, cancel.Token);
        Assert.IsTrue(path.EndsWith("/hello", StringComparison.Ordinal), path);
        Assert.AreEqual(string.Empty, cookie);
        byte[] block = QpackEncoder.Encode([(":status", "200"), ("set-cookie", "next=two")]);
        await WriteFrameAsync(inbound, Http3FrameType.Headers, block, cancel.Token);
        await WriteFrameAsync(inbound, Http3FrameType.Data, "ok"u8.ToArray(), cancel.Token);
        await inbound.CompleteWritesAsync(cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual(3, response.Version.Major);
        CookieCollection stored = cookies.GetCookies(uri);
        Assert.AreEqual(1, stored.Count);
        Assert.AreEqual("session", stored[0]!.Name);
        Assert.AreEqual("one", stored[0]!.Value);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3UsesTargetHostAsTheServerName()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        TaskCompletionSource<string> serverName = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, hello, _) =>
            {
                serverName.TrySetResult(hello.ServerName);
                return ValueTask.FromResult(new ManagedQuicServerConnectionOptions
                {
                    AllowManagedStackUnderFips = true,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                    },
                });
            },
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.SslOptions.TargetHost = "app.example";
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual("app.example", await serverName.Task);
        Assert.AreEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3UsesQuicOptionsLocalEndPoint()
    {
        using Socket reserved = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int localPort = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        reserved.Close();
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.QuicOptions = new ManagedQuicClientConnectionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        };
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual(localPort, server.RemoteEndPoint.Port);
        Assert.AreEqual(IPAddress.Loopback, server.RemoteEndPoint.Address);
        Assert.AreEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3HandshakeTimeoutStopsASilentPeerWhenConnectTimeoutIsInfinite()
    {
        using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        using TitaniumHttpHandler handler = Handler();
        handler.ConnectTimeout = Timeout.InfiniteTimeSpan;
        handler.QuicOptions = new ManagedQuicClientConnectionOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(400),
        };
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        DateTime started = DateTime.UtcNow;
        Http3ConnectException ex = await Assert.ThrowsExactlyAsync<Http3ConnectException>(async () => await client.SendAsync(request));
        Assert.IsInstanceOfType<QuicException>(ex.InnerException);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectIsFollowedOnTheSameConnection()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream first = await server.AcceptInboundStreamAsync(cancel.Token);
        string firstPath = await ReadRequestPathAsync(first, cancel.Token);
        Assert.IsTrue(firstPath.EndsWith("/from", StringComparison.Ordinal));
        byte[] redirect = QpackEncoder.Encode([(":status", "302"), ("location", "/land")]);
        await WriteFrameAsync(first, Http3FrameType.Headers, redirect, cancel.Token);
        await first.CompleteWritesAsync(cancel.Token);
        ManagedQuicStream second = await server.AcceptInboundStreamAsync(cancel.Token);
        string secondPath = await ReadRequestPathAsync(second, cancel.Token);
        Assert.IsTrue(secondPath.EndsWith("/land", StringComparison.Ordinal));
        await ReplyOnAcceptedAsync(second, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual(new Version(3, 0), response.Version);
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3PostRedirectTurnsFoundIntoGetAndKeepsTemporaryRedirect()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage found = new(HttpMethod.Post, address + "/found")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        Task<HttpResponseMessage> pending = client.SendAsync(found, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (string method, string path, string body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/found", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await WriteRedirectAsync(hop, "302", "/land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("GET", method);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, body);
        await ReplyTextAsync(hop, "GET:", cancel.Token);
        using HttpResponseMessage foundResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("GET:", await foundResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(found, foundResponse.RequestMessage);

        using HttpRequestMessage keep = new(HttpMethod.Post, address + "/keep")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        pending = client.SendAsync(keep, cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/keep", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await WriteRedirectAsync(hop, "307", "/land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await ReplyTextAsync(hop, "POST:payload", cancel.Token);
        using HttpResponseMessage keepResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("POST:payload", await keepResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(keep, keepResponse.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3SeeOtherTurnsPostIntoGetAndKeepsHead()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage post = new(HttpMethod.Post, address + "/see")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        Task<HttpResponseMessage> pending = client.SendAsync(post, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (string method, string path, string body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/see", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await WriteRedirectAsync(hop, "303", "/land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("GET", method);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, body);
        await ReplyTextAsync(hop, "GET:", cancel.Token);
        using HttpResponseMessage postResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("GET:", await postResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(post, postResponse.RequestMessage);

        using HttpRequestMessage head = new(HttpMethod.Head, address + "/see-head");
        head.Version = HttpVersion.Version30;
        head.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        pending = client.SendAsync(head, cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("HEAD", method);
        Assert.IsTrue(path.EndsWith("/see-head", StringComparison.Ordinal));
        await WriteRedirectAsync(hop, "303", "/head-land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("HEAD", method);
        Assert.IsTrue(path.EndsWith("/head-land", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, body);
        await ReplySawAsync(hop, "HEAD", cancel.Token);
        using HttpResponseMessage headResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.AreEqual("HEAD", headResponse.Headers.GetValues("X-Saw").Single());
        Assert.AreSame(head, headResponse.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3MovedPermanentlyTurnsPostIntoGetAndPermanentRedirectKeepsIt()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage moved = new(HttpMethod.Post, address + "/moved")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        Task<HttpResponseMessage> pending = client.SendAsync(moved, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (string method, string path, string body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/moved", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await WriteRedirectAsync(hop, "301", "/land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("GET", method);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, body);
        await ReplyTextAsync(hop, "GET:", cancel.Token);
        using HttpResponseMessage movedResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("GET:", await movedResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(moved, movedResponse.RequestMessage);

        using HttpRequestMessage keep = new(HttpMethod.Post, address + "/keep")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        pending = client.SendAsync(keep, cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/keep", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await WriteRedirectAsync(hop, "308", "/land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await ReplyTextAsync(hop, "POST:payload", cancel.Token);
        using HttpResponseMessage keepResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("POST:payload", await keepResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(keep, keepResponse.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3AllowAutoRedirectFalseReturnsTheRedirect()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.AllowAutoRedirect = false;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        string path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        await WriteRedirectAsync(hop, "302", "/land", cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.IsTrue(response.Headers.Location?.OriginalString.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual(new Version(3, 0), response.Version);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectLimitStopsALongerChain()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/a";
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.MaxAutomaticRedirections = 1;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage limited = Request(address);
        Task<HttpResponseMessage> pending = client.SendAsync(limited, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ServeRedirectChainAsync(server, finish: false, cancel.Token);
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await pending);
        Assert.AreEqual("The redirect limit was reached.", ex.Message);
        await server.DisposeAsync();

        accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler longer = Handler();
        longer.MaxAutomaticRedirections = 2;
        using HttpClient again = new(longer, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage allowed = Request(address);
        pending = again.SendAsync(allowed, cancel.Token);
        server = await accepted;
        await ServeRedirectChainAsync(server, finish: true, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("landed", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual(new Version(3, 0), response.Version);
        Assert.AreSame(allowed, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectCopiesACookieHeaderOnlyWhenCookiesAreOff()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from";
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler manual = Handler();
        manual.UseCookies = false;
        using HttpClient manualClient = new(manual, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage manualRequest = Request(address);
        manualRequest.Headers.TryAddWithoutValidation("Cookie", "sid=manual");
        Task<HttpResponseMessage> pending = manualClient.SendAsync(manualRequest, cancel.Token);
        ManagedQuicConnection server = await accepted;
        string copied = await ReadRedirectCookieAsync(server, cancel.Token);
        Assert.AreEqual("sid=manual", copied);
        using HttpResponseMessage manualResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("sid=manual", await manualResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(manualRequest, manualResponse.RequestMessage);
        await server.DisposeAsync();

        accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler stored = Handler();
        stored.UseCookies = true;
        using HttpClient storedClient = new(stored, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage storedRequest = Request(address);
        storedRequest.Headers.TryAddWithoutValidation("Cookie", "sid=manual");
        pending = storedClient.SendAsync(storedRequest, cancel.Token);
        server = await accepted;
        string dropped = await ReadRedirectCookieAsync(server, cancel.Token);
        Assert.AreEqual(string.Empty, dropped);
        using HttpResponseMessage storedResponse = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("none", await storedResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(storedRequest, storedResponse.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectDoesNotCopyTheHostHeader()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string port = listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture);
        string expected = "127.0.0.1:" + port;
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + port + "/from");
        request.Headers.Host = "evil.example";
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        string authority = await ReadRedirectAuthorityAsync(server, expected, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expected, authority);
        Assert.AreEqual(expected, await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectDropsAuthorizationWhenTheHostChanges()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener origin = await ListenHttp3Async(certificate);
        await using ManagedQuicListener next = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string originPort = origin.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture);
        string nextPort = next.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture);
        Task<ManagedQuicConnection> acceptedOrigin = origin.AcceptConnectionAsync(cancel.Token).AsTask();
        Task<ManagedQuicConnection> acceptedNext = next.AcceptConnectionAsync(cancel.Token).AsTask();
        using (TitaniumHttpHandler handler = Handler())
        using (HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) })
        {
            using HttpRequestMessage changed = Request("https://127.0.0.1:" + originPort + "/drop");
            changed.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
            Task<HttpResponseMessage> pending = client.SendAsync(changed, cancel.Token);
            ManagedQuicConnection server = await acceptedOrigin;
            ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
            (string path, string authorization) = await ReadPathAndAuthorizationAsync(hop, cancel.Token);
            Assert.IsTrue(path.EndsWith("/drop", StringComparison.Ordinal));
            Assert.AreEqual("Bearer secret", authorization);
            await WriteRedirectAsync(hop, "302", "https://localhost:" + nextPort + "/see", cancel.Token);
            ManagedQuicConnection landed = await acceptedNext;
            hop = await landed.AcceptInboundStreamAsync(cancel.Token);
            (path, authorization) = await ReadPathAndAuthorizationAsync(hop, cancel.Token);
            Assert.IsTrue(path.EndsWith("/see", StringComparison.Ordinal));
            Assert.AreEqual(string.Empty, authorization);
            await ReplyTextAsync(hop, "absent", cancel.Token);
            using HttpResponseMessage dropped = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
            Assert.AreEqual("absent", await dropped.Content.ReadAsStringAsync(cancel.Token));
            Assert.AreSame(changed, dropped.RequestMessage);
            await server.DisposeAsync();
            await landed.DisposeAsync();
        }

        Task<ManagedQuicConnection> acceptedKeep = next.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler again = Handler();
        using HttpClient keepClient = new(again, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage same = Request("https://localhost:" + nextPort + "/keep");
        same.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        Task<HttpResponseMessage> keptPending = keepClient.SendAsync(same, cancel.Token);
        ManagedQuicConnection keepServer = await acceptedKeep;
        ManagedQuicStream keepHop = await keepServer.AcceptInboundStreamAsync(cancel.Token);
        (string keepPath, string keptAuthorization) = await ReadPathAndAuthorizationAsync(keepHop, cancel.Token);
        Assert.IsTrue(keepPath.EndsWith("/keep", StringComparison.Ordinal));
        Assert.AreEqual("Bearer secret", keptAuthorization);
        await WriteRedirectAsync(keepHop, "302", "/see", cancel.Token);
        keepHop = await keepServer.AcceptInboundStreamAsync(cancel.Token);
        (keepPath, keptAuthorization) = await ReadPathAndAuthorizationAsync(keepHop, cancel.Token);
        Assert.IsTrue(keepPath.EndsWith("/see", StringComparison.Ordinal));
        Assert.AreEqual("Bearer secret", keptAuthorization);
        await ReplyTextAsync(keepHop, "present", cancel.Token);
        using HttpResponseMessage kept = await keptPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("present", await kept.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(same, kept.RequestMessage);
        await keepServer.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3MultipleChoicesTurnsPostIntoGet()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/choice";
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Post, address)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (string method, string path, string body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/choice", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await WriteRedirectAsync(hop, "300", "/land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (method, path, body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("GET", method);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, body);
        await ReplyTextAsync(hop, "GET:", cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("GET:", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual(new Version(3, 0), response.Version);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectToHttpIsFetchedAsHttp11()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int clearPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> clearAccepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        string path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        await WriteRedirectAsync(
            hop,
            "302",
            "http://127.0.0.1:" + clearPort.ToString(CultureInfo.InvariantCulture) + "/land",
            cancel.Token);
        using TcpClient clear = await clearAccepted;
        await using NetworkStream stream = clear.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET /land HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nclear"u8.ToArray(), cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("clear", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreNotEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectReturnsToHttp3AfterACleartextHop()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int clearPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int quicPort = listener.LocalEndPoint.Port;
        Task<TcpClient> clearAccepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + quicPort.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        string path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        await WriteRedirectAsync(
            hop,
            "302",
            "http://127.0.0.1:" + clearPort.ToString(CultureInfo.InvariantCulture) + "/mid",
            cancel.Token);
        using TcpClient clear = await clearAccepted;
        await using NetworkStream stream = clear.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET /mid HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        string back = "https://127.0.0.1:" + quicPort.ToString(CultureInfo.InvariantCulture) + "/land";
        await stream.WriteAsync(
            System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + back + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
            cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal), path);
        await ReplyTextAsync(hop, "landed", cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("landed", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual(new Version(3, 0), response.Version);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3TemporaryRedirectKeepsThePostBodyOnACleartextHop()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int clearPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> clearAccepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Post, "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/keep")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        (string method, string path, string body) = await ReadPostedRequestAsync(hop, cancel.Token);
        Assert.AreEqual("POST", method);
        Assert.IsTrue(path.EndsWith("/keep", StringComparison.Ordinal));
        Assert.AreEqual("payload", body);
        await WriteRedirectAsync(
            hop,
            "307",
            "http://127.0.0.1:" + clearPort.ToString(CultureInfo.InvariantCulture) + "/land",
            cancel.Token);
        using TcpClient clear = await clearAccepted;
        await using NetworkStream stream = clear.GetStream();
        (string headers, string posted) = await ReadHttpRequestWithBodyAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("POST /land HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        Assert.AreEqual("payload", posted);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 12\r\nConnection: close\r\n\r\nPOST:payload"u8.ToArray(), cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("POST:payload", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreNotEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3CleartextHopDecompressesGzip()
    {
        using var encoded = new MemoryStream();
        using (GZipStream gzip = new(encoded, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write("hello"u8);
        }

        byte[] payload = encoded.ToArray();
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int clearPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> clearAccepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        handler.AutomaticDecompression = DecompressionMethods.GZip;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        string path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        await WriteRedirectAsync(
            hop,
            "302",
            "http://127.0.0.1:" + clearPort.ToString(CultureInfo.InvariantCulture) + "/land",
            cancel.Token);
        using TcpClient clear = await clearAccepted;
        await using NetworkStream stream = clear.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET /land HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        Assert.IsTrue(headers.Contains("Accept-Encoding:", StringComparison.OrdinalIgnoreCase), headers);
        byte[] status = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(status, cancel.Token);
        await stream.WriteAsync(payload, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("hello", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreNotEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3CleartextHopSendsAStoredCookie()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int clearPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> clearAccepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        handler.UseCookies = true;
        CookieContainer cookies = new();
        cookies.Add(new Uri("http://127.0.0.1:" + clearPort.ToString(CultureInfo.InvariantCulture) + "/"), new Cookie("sid", "abc"));
        handler.CookieContainer = cookies;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        string path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        await WriteRedirectAsync(
            hop,
            "302",
            "http://127.0.0.1:" + clearPort.ToString(CultureInfo.InvariantCulture) + "/land",
            cancel.Token);
        using TcpClient clear = await clearAccepted;
        await using NetworkStream stream = clear.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET /land HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        Assert.IsTrue(headers.Contains("Cookie: sid=abc", StringComparison.Ordinal), headers);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nabc"u8.ToArray(), cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("abc", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreNotEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3CleartextHopStoresASetCookie()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int clearPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        string clearHost = "http://127.0.0.1:" + clearPort.ToString(CultureInfo.InvariantCulture);
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> clearAccepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        handler.UseCookies = true;
        CookieContainer cookies = new();
        handler.CookieContainer = cookies;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        string path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        await WriteRedirectAsync(hop, "302", clearHost + "/land", cancel.Token);
        using (TcpClient clear = await clearAccepted)
        await using (NetworkStream stream = clear.GetStream())
        {
            string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
            Assert.IsTrue(headers.StartsWith("GET /land HTTP/1.1\r\n", StringComparison.Ordinal), headers);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nSet-Cookie: sid=fromhop; Path=/\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), cancel.Token);
        }

        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual("fromhop", cookies.GetCookies(new Uri(clearHost + "/")).FirstOrDefault(static cookie => cookie.Name == "sid")?.Value);
        await server.DisposeAsync();

        Task<TcpClient> againAccepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        using HttpRequestMessage again = new(HttpMethod.Get, clearHost + "/again")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> second = client.SendAsync(again, cancel.Token);
        using TcpClient next = await againAccepted;
        await using NetworkStream nextStream = next.GetStream();
        string nextHeaders = await ReadHttpHeadersAsync(nextStream, cancel.Token);
        Assert.IsTrue(nextHeaders.StartsWith("GET /again HTTP/1.1\r\n", StringComparison.Ordinal), nextHeaders);
        Assert.IsTrue(nextHeaders.Contains("Cookie: sid=fromhop", StringComparison.Ordinal), nextHeaders);
        await nextStream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nagain"u8.ToArray(), cancel.Token);
        using HttpResponseMessage secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual("again", await secondResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(again, secondResponse.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RedirectStopsWhenTheRequestIsCancelled()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ListenHttp3Async(certificate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var requestCancel = new CancellationTokenSource();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/from");
        Task<HttpResponseMessage> pending = client.SendAsync(request, requestCancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancel.Token);
        string path = await ReadRequestPathAsync(hop, cancel.Token);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        await WriteRedirectAsync(hop, "302", "/land", cancel.Token);
        hop = await server.AcceptInboundStreamAsync(cancel.Token);
        DateTime started = DateTime.UtcNow;
        requestCancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3ReusesOneConnectionForTheSameAuthority()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        using HttpRequestMessage first = Request(address);
        Task<HttpResponseMessage> pending = client.SendAsync(first, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage firstResponse = await pending;
        Assert.AreEqual("ok", await firstResponse.Content.ReadAsStringAsync(cancel.Token));

        using HttpRequestMessage second = Request(address);
        Task<HttpResponseMessage> again = client.SendAsync(second, cancel.Token);
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage secondResponse = await again;
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual("ok", await secondResponse.Content.ReadAsStringAsync(cancel.Token));

        using var late = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            ManagedQuicConnection extra = await listener.AcceptConnectionAsync(late.Token);
            await extra.DisposeAsync();
            Assert.Fail("A second HTTP/3 connection was opened.");
        }
        catch (OperationCanceledException)
        {
        }

        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3GoAwayOpensAnotherConnection()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        using HttpRequestMessage first = Request(address);
        Task<HttpResponseMessage> pending = client.SendAsync(first, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage firstResponse = await pending;
        Assert.AreEqual("ok", await firstResponse.Content.ReadAsStringAsync(cancel.Token));

        await SendGoAwayAsync(server, 0, cancel.Token);
        await handler.Pool.GoAwayObserved.WaitAsync(cancel.Token);
        Assert.AreEqual(0, handler.Pool.ConnectionCount);

        Task<ManagedQuicConnection> next = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using HttpRequestMessage second = Request(address);
        Task<HttpResponseMessage> again = client.SendAsync(second, cancel.Token);
        ManagedQuicConnection server2 = await next;
        await ReplyAsync(server2, cancel.Token);
        using HttpResponseMessage secondResponse = await again;
        Assert.AreEqual("ok", await secondResponse.Content.ReadAsStringAsync(cancel.Token));
        await server.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3GoAwayClosesTheConnectionWhenTheBodyFinishes()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(1, handler.Pool.ConnectionCount);
        await SendGoAwayAsync(server, 0, cancel.Token);
        await handler.Pool.GoAwayObserved.WaitAsync(cancel.Token);
        Assert.AreEqual(1, handler.Pool.ConnectionCount);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual(0, handler.Pool.ConnectionCount);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3DynamicTableDecodesAPeerInsert()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        await using QpackContext table = new(Http3Client.TableCapacity);
        table.MaxTableCapacityFromPeer = Http3Client.TableCapacity;
        table.OutboundEncoderTable.Insert("x-dyn", "yes", null);
        byte[] block = QpackEncoder.Encode([(":status", "200"), ("x-dyn", "yes")], table);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await SendEncoderInsertAsync(server, "x-dyn", "yes", cancel.Token);
        await handler.Pool.EncoderInsertObserved.WaitAsync(cancel.Token);
        await ReplyWithBlockAsync(server, block, cancel.Token);
        using HttpResponseMessage response = await pending;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("x-dyn", out IEnumerable<string>? values));
        Assert.AreEqual("yes", values!.Single());
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3AltSvcUsesTheAdvertisedPort()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        int quicPort = listener.LocalEndPoint.Port;
        await using LoopbackServer server = await LoopbackServer.StartAsync(app =>
        {
            app.MapGet("/tcp", (HttpResponse response) =>
            {
                response.Headers.Append("Alt-Svc", "h3=\":" + quicPort.ToString(CultureInfo.InvariantCulture) + "\"; ma=60");
                return "tcp";
            });
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using TitaniumHttpHandler handler = Handler();
        handler.EnableAltSvc = true;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/tcp";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using HttpResponseMessage firstResponse = await client.SendAsync(first, cancel.Token);
        Assert.AreEqual("tcp", await firstResponse.Content.ReadAsStringAsync(cancel.Token));

        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(second, cancel.Token);
        ManagedQuicConnection quic = await accepted;
        await ReplyAsync(quic, cancel.Token);
        using HttpResponseMessage secondResponse = await pending;
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual(new Version(3, 0), secondResponse.Version);
        Assert.AreEqual("ok", await secondResponse.Content.ReadAsStringAsync(cancel.Token));
        await quic.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3AltSvcMaZeroDoesNotUpgrade()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        int quicPort = listener.LocalEndPoint.Port;
        await using LoopbackServer server = await LoopbackServer.StartAsync(app =>
        {
            app.MapGet("/tcp", (HttpResponse response) =>
            {
                response.Headers.Append("Alt-Svc", "h3=\":" + quicPort.ToString(CultureInfo.InvariantCulture) + "\"; ma=0");
                return "tcp";
            });
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using TitaniumHttpHandler handler = Handler();
        handler.EnableAltSvc = true;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/tcp";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using (HttpResponseMessage firstResponse = await client.SendAsync(first, cancel.Token))
        {
            Assert.AreEqual("tcp", await firstResponse.Content.ReadAsStringAsync(cancel.Token));
        }

        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(second, cancel.Token);
        using var late = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            ManagedQuicConnection extra = await listener.AcceptConnectionAsync(late.Token);
            await extra.DisposeAsync();
            Assert.Fail("HTTP/3 was used after ma=0.");
        }
        catch (OperationCanceledException)
        {
        }

        using HttpResponseMessage secondResponse = await pending;
        Assert.AreEqual("tcp", await secondResponse.Content.ReadAsStringAsync(cancel.Token));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3AltSvcClearDropsTheCachedPort()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        int quicPort = listener.LocalEndPoint.Port;
        int tcpHits = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(app =>
        {
            app.MapGet("/tcp", (HttpResponse response) =>
            {
                int hit = Interlocked.Increment(ref tcpHits);
                if (hit == 1)
                {
                    response.Headers.Append("Alt-Svc", "h3=\":" + quicPort.ToString(CultureInfo.InvariantCulture) + "\"; ma=60");
                    return "tcp";
                }

                if (hit == 2)
                {
                    response.Headers.Append("Alt-Svc", "clear");
                    return "cleared";
                }

                return "stayed";
            });
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using TitaniumHttpHandler handler = Handler();
        handler.EnableAltSvc = true;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/tcp";
        using (HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        })
        {
            using HttpResponseMessage firstResponse = await client.SendAsync(first, cancel.Token);
            Assert.AreEqual("tcp", await firstResponse.Content.ReadAsStringAsync(cancel.Token));
        }

        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using (HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        })
        {
            Task<HttpResponseMessage> pending = client.SendAsync(second, cancel.Token);
            ManagedQuicConnection quic = await accepted;
            await ReplyAsync(quic, cancel.Token);
            using HttpResponseMessage secondResponse = await pending;
            Assert.AreEqual(new Version(3, 0), secondResponse.Version);
            Assert.AreEqual("ok", await secondResponse.Content.ReadAsStringAsync(cancel.Token));
            await quic.DisposeAsync();
        }

        using (HttpRequestMessage clear = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        })
        {
            using HttpResponseMessage clearResponse = await client.SendAsync(clear, cancel.Token);
            Assert.AreEqual("cleared", await clearResponse.Content.ReadAsStringAsync(cancel.Token));
        }

        using var late = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        Task<ManagedQuicConnection> refused = listener.AcceptConnectionAsync(late.Token).AsTask();
        using HttpRequestMessage third = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        Task<HttpResponseMessage> stayed = client.SendAsync(third, cancel.Token);
        try
        {
            ManagedQuicConnection extra = await refused;
            await extra.DisposeAsync();
            Assert.Fail("HTTP/3 was used after Alt-Svc: clear.");
        }
        catch (OperationCanceledException)
        {
        }

        using HttpResponseMessage thirdResponse = await stayed;
        Assert.AreEqual("stayed", await thirdResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreNotEqual(3, thirdResponse.Version.Major);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3AltSvcIgnoresADifferentHost()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        int quicPort = listener.LocalEndPoint.Port;
        await using LoopbackServer server = await LoopbackServer.StartAsync(app =>
        {
            app.MapGet("/tcp", (HttpResponse response) =>
            {
                response.Headers.Append(
                    "Alt-Svc",
                    "h3=\"other.example:" + quicPort.ToString(CultureInfo.InvariantCulture) + "\"; ma=60");
                return "tcp";
            });
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using TitaniumHttpHandler handler = Handler();
        handler.EnableAltSvc = true;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/tcp";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using HttpResponseMessage firstResponse = await client.SendAsync(first, cancel.Token);
        Assert.AreEqual("tcp", await firstResponse.Content.ReadAsStringAsync(cancel.Token));
        using var late = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        Task<ManagedQuicConnection> refused = listener.AcceptConnectionAsync(late.Token).AsTask();
        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(second, cancel.Token);
        try
        {
            ManagedQuicConnection extra = await refused;
            await extra.DisposeAsync();
            Assert.Fail("HTTP/3 was used for a different Alt-Svc host.");
        }
        catch (OperationCanceledException)
        {
        }

        using HttpResponseMessage secondResponse = await pending;
        Assert.AreEqual("tcp", await secondResponse.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreNotEqual(3, secondResponse.Version.Major);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3AltSvcFallsBackWhenTheUpgradeHandshakeFails()
    {
        using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int quicPort = ((IPEndPoint)udp.LocalEndPoint!).Port;
        await using LoopbackServer server = await LoopbackServer.StartAsync(app =>
        {
            app.MapGet("/tcp", (HttpResponse response) =>
            {
                response.Headers.Append("Alt-Svc", "h3=\":" + quicPort.ToString(CultureInfo.InvariantCulture) + "\"; ma=60");
                return "tcp";
            });
        });
        using TitaniumHttpHandler handler = Handler();
        handler.EnableAltSvc = true;
        handler.ConnectTimeout = TimeSpan.FromMilliseconds(400);
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/tcp";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using HttpResponseMessage firstResponse = await client.SendAsync(first);
        Assert.AreEqual("tcp", await firstResponse.Content.ReadAsStringAsync());
        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        DateTime started = DateTime.UtcNow;
        using HttpResponseMessage secondResponse = await client.SendAsync(second);
        string body = await secondResponse.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode, body);
        Assert.AreEqual("tcp", body);
        Assert.AreNotEqual(3, secondResponse.Version.Major);
        Assert.AreSame(second, secondResponse.RequestMessage);
        Assert.AreEqual("https", secondResponse.RequestMessage!.RequestUri!.Scheme);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3HeadersReturnBeforeTheBody()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream stream = await HeadersAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await WriteFrameAsync(stream, Http3FrameType.Data, "ok"u8.ToArray(), cancel.Token);
        await stream.CompleteWritesAsync(cancel.Token);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3TrailersArriveWhileTheBodyIsRead()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream stream = await HeadersAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await WriteFrameAsync(stream, Http3FrameType.Data, "ok"u8.ToArray(), cancel.Token);
        byte[] trailer = QpackEncoder.Encode([("x-sum", "2")]);
        await WriteFrameAsync(stream, Http3FrameType.Headers, trailer, cancel.Token);
        await stream.CompleteWritesAsync(cancel.Token);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.IsTrue(response.TrailingHeaders.TryGetValues("x-sum", out IEnumerable<string>? values));
        Assert.AreEqual("2", values!.Single());
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3IdleConnectionIsNotReused()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        using TitaniumHttpHandler handler = Handler();
        handler.PooledConnectionIdleTimeout = TimeSpan.FromMilliseconds(200);
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        Task<ManagedQuicConnection> firstAccepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using HttpRequestMessage firstRequest = Request(address);
        Task<HttpResponseMessage> firstPending = client.SendAsync(firstRequest, cancel.Token);
        ManagedQuicConnection firstServer = await firstAccepted;
        await ReplyAsync(firstServer, cancel.Token);
        using HttpResponseMessage firstResponse = await firstPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancel.Token);
        Task<ManagedQuicConnection> secondAccepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using HttpRequestMessage secondRequest = Request(address);
        Task<HttpResponseMessage> secondPending = client.SendAsync(secondRequest, cancel.Token);
        ManagedQuicConnection secondServer = await secondAccepted.WaitAsync(TimeSpan.FromSeconds(3), cancel.Token);
        await ReplyAsync(secondServer, cancel.Token);
        using HttpResponseMessage secondResponse = await secondPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        await firstServer.DisposeAsync();
        await secondServer.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3ConnectionLifetimeOpensAnotherConnection()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        using TitaniumHttpHandler handler = Handler();
        handler.PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan;
        handler.PooledConnectionLifetime = TimeSpan.FromMilliseconds(200);
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        Task<ManagedQuicConnection> firstAccepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using HttpRequestMessage firstRequest = Request(address);
        Task<HttpResponseMessage> firstPending = client.SendAsync(firstRequest, cancel.Token);
        ManagedQuicConnection firstServer = await firstAccepted;
        await ReplyAsync(firstServer, cancel.Token);
        using HttpResponseMessage firstResponse = await firstPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancel.Token);
        Task<ManagedQuicConnection> secondAccepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using HttpRequestMessage secondRequest = Request(address);
        Task<HttpResponseMessage> secondPending = client.SendAsync(secondRequest, cancel.Token);
        ManagedQuicConnection secondServer = await secondAccepted.WaitAsync(TimeSpan.FromSeconds(3), cancel.Token);
        await ReplyAsync(secondServer, cancel.Token);
        using HttpResponseMessage secondResponse = await secondPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        await firstServer.DisposeAsync();
        await secondServer.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3SeventeenthRequestOpensAnotherConnection()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        Task<ManagedQuicConnection> firstAccepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        ManagedQuicConnection firstServer = null!;
        for (int i = 0; i < Http3Pool.StreamCap; i++)
        {
            using HttpRequestMessage request = Request(address);
            Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
            if (i == 0)
            {
                firstServer = await firstAccepted;
            }

            await ReplyAsync(firstServer, cancel.Token);
            using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        Task<ManagedQuicConnection> secondAccepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using HttpRequestMessage extra = Request(address);
        Task<HttpResponseMessage> extraPending = client.SendAsync(extra, cancel.Token);
        ManagedQuicConnection secondServer = await secondAccepted.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await ReplyAsync(secondServer, cancel.Token);
        using HttpResponseMessage extraResponse = await extraPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, extraResponse.StatusCode);
        await firstServer.DisposeAsync();
        await secondServer.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3WaitsInsteadOfPassingMaxConnectionsPerServer()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        using TitaniumHttpHandler handler = Handler();
        handler.MaxConnectionsPerServer = 1;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        Task<ManagedQuicConnection> firstAccepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        ManagedQuicConnection firstServer = null!;
        for (int i = 0; i < Http3Pool.StreamCap - 1; i++)
        {
            using HttpRequestMessage request = Request(address);
            Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
            if (i == 0)
            {
                firstServer = await firstAccepted;
            }

            await ReplyAsync(firstServer, cancel.Token);
            using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        using HttpRequestMessage heldRequest = Request(address);
        Task<HttpResponseMessage> heldPending = client.SendAsync(heldRequest, cancel.Token);
        ManagedQuicStream held = await firstServer.AcceptInboundStreamAsync(cancel.Token);
        using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        Task<ManagedQuicConnection> refused = listener.AcceptConnectionAsync(probe.Token).AsTask();
        using HttpRequestMessage extra = Request(address);
        Task<HttpResponseMessage> extraPending = client.SendAsync(extra, cancel.Token);
        try
        {
            ManagedQuicConnection unexpected = await refused;
            await unexpected.DisposeAsync();
            Assert.Fail("A second HTTP/3 connection was opened while the first was busy.");
        }
        catch (OperationCanceledException)
        {
        }

        Task<ManagedQuicConnection> replacement = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await ReplyOnAcceptedAsync(held, cancel.Token);
        using HttpResponseMessage heldResponse = await heldPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, heldResponse.StatusCode);
        ManagedQuicConnection secondServer = await replacement.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await ReplyAsync(secondServer, cancel.Token);
        using HttpResponseMessage extraResponse = await extraPending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, extraResponse.StatusCode);
        await firstServer.DisposeAsync();
        await secondServer.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3BodyReadStopsWhenCancelled()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using HttpRequestMessage request = Request("https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await HeadersAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await using Stream body = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var readCancel = new CancellationTokenSource();
        ValueTask<int> read = body.ReadAsync(new byte[8], readCancel.Token);
        readCancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await read);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3RequestInsertsAFieldThatIsNotInTheStaticTable()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        Task<HttpResponseMessage> first = client.SendAsync(Request(address), cancel.Token);
        ManagedQuicConnection server = await accepted;
        QpackContext table = new(4096);
        Task reading = ReadClientEncoderAsync(server, table, cancel.Token);
        await SendQpackSettingsAsync(server, cancel.Token);
        await ReplyOnceAsync(server, cancel.Token);
        using HttpResponseMessage warmed = await first.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, warmed.StatusCode);
        await handler.Pool.PeerSettingsObserved.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);

        using HttpRequestMessage request = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TryAddWithoutValidation("x-row", "alpha");
        Task<HttpResponseMessage> second = client.SendAsync(request, cancel.Token);
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        byte[]? headers = null;
        while (await ReadFrameAsync(inbound, cancel.Token) is byte[] frame)
        {
            headers = frame;
            break;
        }

        Assert.IsNotNull(headers);
        List<(string Name, string Value)> fields = await DecodeWhenInsertedAsync(headers!, table, cancel.Token);
        Assert.IsTrue(fields.Exists(static field => field.Name == "x-row" && field.Value == "alpha"));
        await ReplyOnStreamAsync(inbound, cancel.Token);
        using HttpResponseMessage response = await second.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        cancel.Cancel();
        await reading;
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3LargeUploadAndDownloadMatchTheirHashes()
    {
        byte[] upload = Pattern(1024 * 1024);
        byte[] download = Pattern(1024 * 1024 + 17);
        string uploadHash = Convert.ToHexString(SHA256.HashData(upload));
        string downloadHash = Convert.ToHexString(SHA256.HashData(download));
        using X509Certificate2 certificate = CreateHttp3Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Post, "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/blob")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(upload),
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        ManagedQuicConnection server = await accepted;
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        using IncrementalHash received = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (await ReadTypedFrameAsync(inbound, cancel.Token) is (ulong type, byte[] payload))
        {
            if (type == Http3FrameType.Data)
            {
                received.AppendData(payload);
            }
        }

        byte[] block = QpackEncoder.Encode([(":status", "200"), ("x-upload-sha256", Convert.ToHexString(received.GetHashAndReset()))]);
        await WriteFrameAsync(inbound, Http3FrameType.Headers, block, cancel.Token);
        Task writing = WriteDownloadAsync(inbound, download, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("x-upload-sha256", out IEnumerable<string>? hashes));
        Assert.AreEqual(uploadHash, hashes!.Single());
        await using Stream body = await response.Content.ReadAsStreamAsync(cancel.Token);
        using IncrementalHash got = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await body.ReadAsync(chunk, cancel.Token);
            if (count == 0)
            {
                break;
            }

            got.AppendData(chunk.AsSpan(0, count));
        }

        Assert.AreEqual(downloadHash, Convert.ToHexString(got.GetHashAndReset()));
        Assert.AreSame(request, response.RequestMessage);
        await writing;
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11AgainstLoopbackReturnsTheBody()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/", (HttpRequest request) => request.Protocol);
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:" + server.Port.ToString() + "/")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("HTTP/1.1", await response.Content.ReadAsStringAsync());
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11PropertyChangeAfterTheFirstRequestThrows()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/ping", () => Results.Text("pong"));
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/ping")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.AreEqual("pong", await response.Content.ReadAsStringAsync());
        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(() => handler.MaxConnectionsPerServer = 2);
        Assert.AreEqual("This instance has already started one or more requests. Properties can only be modified before sending the first request.", ex.Message);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11BodyReadStopsWhenCancelled()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/slow", async context =>
            {
                context.Response.ContentLength = 8;
                await context.Response.Body.FlushAsync();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            });
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/slow")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        await using Stream body = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var readCancel = new CancellationTokenSource();
        ValueTask<int> read = body.ReadAsync(new byte[8], readCancel.Token);
        readCancel.Cancel();
        long started = Environment.TickCount64;
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await read);
        Assert.IsTrue(Environment.TickCount64 - started < 2000);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RequestStopsWhenCancelledBeforeTheStatusLine()
    {
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LoopbackServer server = await LoopbackServer.StartAsync(app =>
        {
            app.MapGet("/wait", async context =>
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            });
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/wait")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var requestCancel = new CancellationTokenSource();
        Task<HttpResponseMessage> pending = client.SendAsync(request, requestCancel.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long started = Environment.TickCount64;
        requestCancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
        Assert.IsTrue(Environment.TickCount64 - started < 2000);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11ReusesAConnectionUntilItsLifetime()
    {
        int connections = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/ping", () => Results.Text("pong")),
            () => Interlocked.Increment(ref connections));
        using TitaniumHttpHandler handler = Handler();
        handler.PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan;
        handler.PooledConnectionLifetime = TimeSpan.FromMilliseconds(300);
        using HttpClient client = new(handler, disposeHandler: false);
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/ping";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using (HttpResponseMessage firstResponse = await client.SendAsync(first))
        {
            Assert.AreEqual("pong", await firstResponse.Content.ReadAsStringAsync());
        }

        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using (HttpResponseMessage secondResponse = await client.SendAsync(second))
        {
            Assert.AreEqual("pong", await secondResponse.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(1, connections);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        using HttpRequestMessage third = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage thirdResponse = await client.SendAsync(third);
        Assert.AreEqual("pong", await thirdResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(2, connections);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11WaitsInsteadOfPassingMaxConnectionsPerServer()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int connections = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            app => app.MapGet("/hold", async () =>
            {
                started.TrySetResult();
                await release.Task;
                return Results.Text("done");
            }),
            () => Interlocked.Increment(ref connections));
        using TitaniumHttpHandler handler = Handler();
        handler.MaxConnectionsPerServer = 1;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hold";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> firstPending = client.SendAsync(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, connections);
        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> secondPending = client.SendAsync(second);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.AreEqual(1, connections);
        Assert.IsFalse(secondPending.IsCompleted);
        release.SetResult();
        using HttpResponseMessage firstResponse = await firstPending.WaitAsync(TimeSpan.FromSeconds(5));
        using HttpResponseMessage secondResponse = await secondPending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("done", await firstResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("done", await secondResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(1, connections);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RequestGoesThroughTheProxy()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> originAccepted = origin.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<TcpClient> proxyAccepted = proxy.AcceptTcpClientAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = true;
        handler.Proxy = new WebProxy("http://127.0.0.1:" + proxyPort.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string originUri = "http://127.0.0.1:" + originPort.ToString(CultureInfo.InvariantCulture) + "/hello";
        using HttpRequestMessage request = new(HttpMethod.Get, originUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        using TcpClient accepted = await proxyAccepted.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await using NetworkStream stream = accepted.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET " + originUri + " HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nproxy"u8.ToArray(), cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("proxy", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual(originUri, response.RequestMessage!.RequestUri!.AbsoluteUri);
        Assert.IsFalse(originAccepted.IsCompleted);
        origin.Stop();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11UseProxyFalseIgnoresTheProxy()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> originAccepted = origin.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<TcpClient> proxyAccepted = proxy.AcceptTcpClientAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        handler.Proxy = new WebProxy("http://127.0.0.1:" + proxyPort.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "http://127.0.0.1:" + originPort.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        using TcpClient accepted = await originAccepted.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await using NetworkStream stream = accepted.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET /hello HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\norigin"u8.ToArray(), cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("origin", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(request, response.RequestMessage);
        Assert.IsFalse(proxyAccepted.IsCompleted);
        proxy.Stop();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11HttpsRequestTunnelsThroughTheProxy()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/hello", () => Results.Text("tunneled"));
        });
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> proxyAccepted = proxy.AcceptTcpClientAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = true;
        handler.Proxy = new WebProxy("http://127.0.0.1:" + proxyPort.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello";
        using HttpRequestMessage request = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        using TcpClient accepted = await proxyAccepted.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await using NetworkStream stream = accepted.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        string connect = "CONNECT 127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + " HTTP/1.1\r\n";
        Assert.IsTrue(headers.StartsWith(connect, StringComparison.Ordinal), headers);
        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancel.Token);
        using TcpClient origin = new();
        await origin.ConnectAsync(IPAddress.Loopback, server.Port, cancel.Token);
        await using NetworkStream originStream = origin.GetStream();
        Task forward = CopyBytesAsync(stream, originStream, cancel.Token);
        Task backward = CopyBytesAsync(originStream, stream, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("tunneled", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
        cancel.Cancel();
        try
        {
            await Task.WhenAll(forward, backward);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11ProxyRefusalFailsBeforeTheTlsHandshake()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> originAccepted = origin.AcceptTcpClientAsync(cancel.Token).AsTask();
        Task<TcpClient> proxyAccepted = proxy.AcceptTcpClientAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = true;
        handler.Proxy = new WebProxy("http://127.0.0.1:" + proxyPort.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + originPort.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        using TcpClient accepted = await proxyAccepted.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        await using NetworkStream stream = accepted.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("CONNECT 127.0.0.1:" + originPort.ToString(CultureInfo.InvariantCulture) + " HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        long started = Environment.TickCount64;
        await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancel.Token);
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await pending);
        Assert.IsTrue(Environment.TickCount64 - started < 2000);
        Assert.IsTrue(HasMessage(ex, "The proxy refused the tunnel."), ex.ToString());
        Assert.IsFalse(originAccepted.IsCompleted);
        origin.Stop();
    }

    private static bool HasMessage(Exception exception, string text)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task CopyBytesAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int count = await from.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RequestIsCleartext()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> accepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        using TcpClient clear = await accepted;
        await using NetworkStream stream = clear.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET /hello HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nplain"u8.ToArray(), cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("plain", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("http", response.RequestMessage!.RequestUri!.Scheme);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task HttpVersion3OnAnHttpUriIsCleartextHttp11()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TcpClient> accepted = tcp.AcceptTcpClientAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        handler.UseProxy = false;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        using TcpClient clear = await accepted;
        await using NetworkStream stream = clear.GetStream();
        string headers = await ReadHttpHeadersAsync(stream, cancel.Token);
        Assert.IsTrue(headers.StartsWith("GET /hello HTTP/1.1\r\n", StringComparison.Ordinal), headers);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nplain"u8.ToArray(), cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("plain", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreNotEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("http", response.RequestMessage!.RequestUri!.Scheme);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RedirectsAcrossSchemesAndKeepsTheCookie()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/from", (HttpContext context) =>
            {
                int port = context.Connection.LocalPort;
                context.Response.Headers.Append("Set-Cookie", "sid=abc; Path=/");
                context.Response.Redirect("https://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/land");
                return Task.CompletedTask;
            });
            app.MapGet("/land", async context =>
            {
                if (context.Request.Cookies["sid"] != "abc")
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                byte[] plain = "hello"u8.ToArray();
                using MemoryStream encoded = new();
                using (GZipStream gzip = new(encoded, CompressionLevel.Fastest, leaveOpen: true))
                {
                    gzip.Write(plain);
                }

                context.Response.Headers.ContentEncoding = "gzip";
                context.Response.ContentType = "text/plain";
                await context.Response.Body.WriteAsync(encoded.ToArray());
            });
        });
        using TitaniumHttpHandler handler = Handler();
        handler.AutomaticDecompression = DecompressionMethods.GZip;
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:" + server.Port.ToString() + "/from")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("hello", body);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RedirectDropsAuthorizationWhenTheHostChanges()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/drop", (HttpContext context) =>
            {
                int port = context.Connection.LocalPort;
                context.Response.Redirect("https://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/see");
                return Task.CompletedTask;
            });
            app.MapGet("/keep", (HttpContext context) =>
            {
                int port = context.Connection.LocalPort;
                context.Response.Redirect("https://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/see");
                return Task.CompletedTask;
            });
            app.MapGet("/see", (HttpRequest request) =>
                request.Headers.Authorization.Count == 0 ? "absent" : "present");
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        string port = server.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage changed = new(HttpMethod.Get, "https://127.0.0.1:" + port + "/drop")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        changed.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        using HttpResponseMessage dropped = await client.SendAsync(changed);
        Assert.AreEqual("absent", await dropped.Content.ReadAsStringAsync());
        Assert.AreSame(changed, dropped.RequestMessage);
        using HttpRequestMessage same = new(HttpMethod.Get, "https://localhost:" + port + "/keep")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        same.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        using HttpResponseMessage kept = await client.SendAsync(same);
        Assert.AreEqual("present", await kept.Content.ReadAsStringAsync());
        Assert.AreSame(same, kept.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11PostRedirectTurnsFoundIntoGetAndKeepsTemporaryRedirect()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapPost("/found", (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "/land";
                return Task.CompletedTask;
            });
            app.MapPost("/keep", (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = "/land";
                return Task.CompletedTask;
            });
            app.MapMethods("/land", ["GET", "POST"], async context =>
            {
                using StreamReader reader = new(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                await context.Response.WriteAsync(context.Request.Method + ":" + body);
            });
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage found = new(HttpMethod.Post, address + "/found")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        using HttpResponseMessage foundResponse = await client.SendAsync(found);
        Assert.AreEqual("GET:", await foundResponse.Content.ReadAsStringAsync());
        Assert.AreSame(found, foundResponse.RequestMessage);
        using HttpRequestMessage keep = new(HttpMethod.Post, address + "/keep")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        using HttpResponseMessage keepResponse = await client.SendAsync(keep);
        Assert.AreEqual("POST:payload", await keepResponse.Content.ReadAsStringAsync());
        Assert.AreSame(keep, keepResponse.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11MovedPermanentlyTurnsPostIntoGetAndPermanentRedirectKeepsIt()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapPost("/moved", (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                context.Response.Headers.Location = "/land";
                return Task.CompletedTask;
            });
            app.MapPost("/keep", (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = "/land";
                return Task.CompletedTask;
            });
            app.MapMethods("/land", ["GET", "POST"], async context =>
            {
                using StreamReader reader = new(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                await context.Response.WriteAsync(context.Request.Method + ":" + body);
            });
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage moved = new(HttpMethod.Post, address + "/moved")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        using HttpResponseMessage movedResponse = await client.SendAsync(moved);
        Assert.AreEqual("GET:", await movedResponse.Content.ReadAsStringAsync());
        Assert.AreSame(moved, movedResponse.RequestMessage);
        using HttpRequestMessage keep = new(HttpMethod.Post, address + "/keep")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        using HttpResponseMessage keepResponse = await client.SendAsync(keep);
        Assert.AreEqual("POST:payload", await keepResponse.Content.ReadAsStringAsync());
        Assert.AreSame(keep, keepResponse.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11SeeOtherTurnsPostIntoGetAndKeepsHead()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapPost("/see", (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status303SeeOther;
                context.Response.Headers.Location = "/land";
                return Task.CompletedTask;
            });
            app.MapMethods("/see-head", [HttpMethods.Head], (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status303SeeOther;
                context.Response.Headers.Location = "/head-land";
                return Task.CompletedTask;
            });
            app.MapGet("/land", async context =>
            {
                using StreamReader reader = new(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                await context.Response.WriteAsync(context.Request.Method + ":" + body);
            });
            app.MapMethods("/head-land", [HttpMethods.Head], (HttpContext context) =>
            {
                context.Response.Headers["X-Saw"] = context.Request.Method;
                return Task.CompletedTask;
            });
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage post = new(HttpMethod.Post, address + "/see")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        using HttpResponseMessage postResponse = await client.SendAsync(post);
        Assert.AreEqual("GET:", await postResponse.Content.ReadAsStringAsync());
        Assert.AreSame(post, postResponse.RequestMessage);
        using HttpRequestMessage head = new(HttpMethod.Head, address + "/see-head")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage headResponse = await client.SendAsync(head);
        Assert.AreEqual(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.AreEqual("HEAD", headResponse.Headers.GetValues("X-Saw").Single());
        Assert.AreSame(head, headResponse.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RedirectLimitStopsALongerChain()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/a", (HttpContext context) =>
            {
                context.Response.Redirect("/b");
                return Task.CompletedTask;
            });
            app.MapGet("/b", (HttpContext context) =>
            {
                context.Response.Redirect("/c");
                return Task.CompletedTask;
            });
            app.MapGet("/c", () => "landed");
        });
        using TitaniumHttpHandler handler = Handler();
        handler.MaxAutomaticRedirections = 1;
        using HttpClient client = new(handler, disposeHandler: false);
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/a";
        using HttpRequestMessage limited = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.SendAsync(limited));
        Assert.AreEqual("The redirect limit was reached.", ex.Message);
        using TitaniumHttpHandler longer = Handler();
        longer.MaxAutomaticRedirections = 2;
        using HttpClient again = new(longer, disposeHandler: false);
        using HttpRequestMessage allowed = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await again.SendAsync(allowed);
        Assert.AreEqual("landed", await response.Content.ReadAsStringAsync());
        Assert.AreSame(allowed, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11AllowAutoRedirectFalseReturnsTheRedirect()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/from", (HttpContext context) =>
            {
                context.Response.Redirect("/land");
                return Task.CompletedTask;
            });
            app.MapGet("/land", () => "landed");
        });
        using TitaniumHttpHandler handler = Handler();
        handler.AllowAutoRedirect = false;
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/from")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.IsTrue(response.Headers.Location?.OriginalString.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RedirectCopiesACookieHeaderOnlyWhenCookiesAreOff()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/from", (HttpContext context) =>
            {
                context.Response.Redirect("/land");
                return Task.CompletedTask;
            });
            app.MapGet("/land", (HttpRequest request) =>
            {
                string cookie = request.Headers.Cookie.ToString();
                return cookie.Length == 0 ? "none" : cookie;
            });
        });
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/from";
        using TitaniumHttpHandler manual = Handler();
        manual.UseCookies = false;
        using HttpClient manualClient = new(manual, disposeHandler: false);
        using HttpRequestMessage manualRequest = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        manualRequest.Headers.TryAddWithoutValidation("Cookie", "sid=manual");
        using HttpResponseMessage manualResponse = await manualClient.SendAsync(manualRequest);
        Assert.AreEqual("sid=manual", await manualResponse.Content.ReadAsStringAsync());
        using TitaniumHttpHandler stored = Handler();
        stored.UseCookies = true;
        using HttpClient storedClient = new(stored, disposeHandler: false);
        using HttpRequestMessage storedRequest = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        storedRequest.Headers.TryAddWithoutValidation("Cookie", "sid=manual");
        using HttpResponseMessage storedResponse = await storedClient.SendAsync(storedRequest);
        Assert.AreEqual("none", await storedResponse.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11RedirectDoesNotCopyTheHostHeader()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/from", (HttpContext context) =>
            {
                context.Response.Redirect("/land");
                return Task.CompletedTask;
            });
            app.MapGet("/land", (HttpRequest request) => request.Host.Value);
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        string port = server.Port.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + port + "/from")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Host = "evil.example";
        using HttpResponseMessage response = await client.SendAsync(request);
        string host = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, host);
        Assert.AreEqual("127.0.0.1:" + port, host);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11LargeUploadAndDownloadMatchTheirHashes()
    {
        byte[] upload = Pattern(1024 * 1024);
        byte[] download = Pattern(1024 * 1024 + 17);
        string uploadHash = Convert.ToHexString(SHA256.HashData(upload));
        string downloadHash = Convert.ToHexString(SHA256.HashData(download));
        await using LoopbackServer server = await LoopbackServer.StartAsync(app =>
        {
            app.MapPost("/blob", async context =>
            {
                using IncrementalHash received = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[8192];
                while (true)
                {
                    int count = await context.Request.Body.ReadAsync(buffer);
                    if (count == 0)
                    {
                        break;
                    }

                    received.AppendData(buffer.AsSpan(0, count));
                }

                context.Response.Headers.Append("X-Upload-Sha256", Convert.ToHexString(received.GetHashAndReset()));
                context.Response.ContentLength = download.Length;
                await context.Response.Body.WriteAsync(download);
            });
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Post, "https://localhost:" + server.Port.ToString() + "/blob")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(upload),
        };

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("X-Upload-Sha256", out IEnumerable<string>? hashes));
        Assert.AreEqual(uploadHash, hashes!.Single());
        await using Stream body = await response.Content.ReadAsStreamAsync();
        using IncrementalHash got = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await body.ReadAsync(chunk);
            if (count == 0)
            {
                break;
            }

            got.AppendData(chunk.AsSpan(0, count));
        }

        Assert.AreEqual(downloadHash, Convert.ToHexString(got.GetHashAndReset()));
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task ConnectTimeoutStopsASilentTcpHandshake()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> held = listener.AcceptTcpClientAsync();
        using TitaniumHttpHandler handler = Handler();
        handler.ConnectTimeout = TimeSpan.FromMilliseconds(400);
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        DateTime started = DateTime.UtcNow;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.SendAsync(request));
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        listener.Stop();
        if (held.IsCompletedSuccessfully)
        {
            held.Result.Dispose();
        }
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task ConnectTimeoutStopsASilentHttp3Handshake()
    {
        using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        using TitaniumHttpHandler handler = Handler();
        handler.ConnectTimeout = TimeSpan.FromMilliseconds(400);
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        DateTime started = DateTime.UtcNow;
        Http3ConnectException ex = await Assert.ThrowsExactlyAsync<Http3ConnectException>(async () => await client.SendAsync(request));
        Assert.IsInstanceOfType<QuicException>(ex.InnerException);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http2ExactDoesNotFallBackWhenTheServerResetsTheStream()
    {
        int connections = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/hello", () => Results.Text("http11")),
            () => Interlocked.Increment(ref connections));
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.SendAsync(request));
        Assert.AreEqual(HttpRequestError.HttpProtocolError, ex.HttpRequestError);
        Assert.AreEqual(1, connections);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http2PostDoesNotRetryWhenTheServerResetsTheStream()
    {
        int connections = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapPost("/hello", () => Results.Text("retried")),
            () => Interlocked.Increment(ref connections));
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Post, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent("payload"),
        };
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.SendAsync(request));
        Assert.AreEqual(HttpRequestError.HttpProtocolError, ex.HttpRequestError);
        Assert.AreEqual(1, connections);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http2PostIsRetriedWhenAlpnSelectsHttp11()
    {
        int connections = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapPost("/hello", async (HttpRequest httpRequest) =>
            {
                using StreamReader reader = new(httpRequest.Body);
                string body = await reader.ReadToEndAsync();
                return Results.Text(httpRequest.Protocol + ":" + body);
            }),
            () => Interlocked.Increment(ref connections),
            HttpProtocols.Http1);
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Post, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent("payload"),
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("HTTP/1.1:payload", body);
        Assert.AreEqual(2, connections);
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http2ExactIsNotRetriedWhenAlpnSelectsHttp11()
    {
        int connections = 0;
        int seen = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            app => app.MapPost("/hello", () =>
            {
                Interlocked.Increment(ref seen);
                return Results.Text("seen");
            }),
            () => Interlocked.Increment(ref connections),
            HttpProtocols.Http1);
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Post, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("payload"),
        };
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.SendAsync(request));
        Assert.IsTrue(HasMessage(ex, "The server selected HTTP/1.1."), ex.ToString());
        Assert.AreEqual(1, connections);
        Assert.AreEqual(0, seen);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11OrHigherFallsBackWhenAlpnSelectsHttp11AndReusesTheConnection()
    {
        int connections = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/ping", (HttpRequest httpRequest) => Results.Text(httpRequest.Protocol)),
            () => Interlocked.Increment(ref connections),
            HttpProtocols.Http1);
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/ping";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using (HttpResponseMessage firstResponse = await client.SendAsync(first))
        {
            Assert.AreEqual("HTTP/1.1", await firstResponse.Content.ReadAsStringAsync());
            Assert.AreSame(first, firstResponse.RequestMessage);
        }

        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using (HttpResponseMessage secondResponse = await client.SendAsync(second))
        {
            Assert.AreEqual("HTTP/1.1", await secondResponse.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(2, connections);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11OrHigherOnAnotherPortStillTriesAHigherVersion()
    {
        int firstConnections = 0;
        int secondConnections = 0;
        await using LoopbackServer first = await LoopbackServer.StartAsync(
            static app => app.MapGet("/ping", () => Results.Text("one")),
            () => Interlocked.Increment(ref firstConnections),
            HttpProtocols.Http1);
        await using LoopbackServer second = await LoopbackServer.StartAsync(
            static app => app.MapGet("/ping", () => Results.Text("two")),
            () => Interlocked.Increment(ref secondConnections),
            HttpProtocols.Http1);
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage firstRequest = new(HttpMethod.Get, "https://127.0.0.1:" + first.Port.ToString(CultureInfo.InvariantCulture) + "/ping")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using (HttpResponseMessage firstResponse = await client.SendAsync(firstRequest))
        {
            Assert.AreEqual("one", await firstResponse.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(2, firstConnections);
        using HttpRequestMessage secondRequest = new(HttpMethod.Get, "https://127.0.0.1:" + second.Port.ToString(CultureInfo.InvariantCulture) + "/ping")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        using (HttpResponseMessage secondResponse = await client.SendAsync(secondRequest))
        {
            Assert.AreEqual("two", await secondResponse.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(2, secondConnections);
        Assert.AreEqual(2, firstConnections);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11PresentsTheClientCertificate()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/who", (HttpContext context) =>
            {
                string subject = context.Connection.ClientCertificate?.Subject ?? "none";
                return Results.Text(subject);
            }),
            null,
            HttpProtocols.Http1,
            requestClientCertificate: true);
        using X509Certificate2 clientCertificate = CreateClientCertificate();
        using TitaniumHttpHandler handler = Handler();
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/who")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.IsTrue(body.Contains("CN=client", StringComparison.Ordinal), body);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11SelectionCallbackChoosesTheClientCertificate()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/who", (HttpContext context) =>
            {
                string subject = context.Connection.ClientCertificate?.Subject ?? "none";
                return Results.Text(subject);
            }),
            null,
            HttpProtocols.Http1,
            requestClientCertificate: true);
        using X509Certificate2 first = CreateClientCertificate("first");
        using X509Certificate2 second = CreateClientCertificate("second");
        using TitaniumHttpHandler handler = Handler();
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { first, second };
        handler.SslOptions.LocalCertificateSelectionCallback = static (_, _, localCertificates, _, _) => localCertificates[1];
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/who")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.IsTrue(body.Contains("CN=second", StringComparison.Ordinal), body);
        Assert.IsFalse(body.Contains("CN=first", StringComparison.Ordinal), body);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11SkipsAClientCertificateWithoutAPrivateKey()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/who", (HttpContext context) =>
            {
                string subject = context.Connection.ClientCertificate?.Subject ?? "none";
                return Results.Text(subject);
            }),
            null,
            HttpProtocols.Http1,
            requestClientCertificate: true);
        using X509Certificate2 source = CreateClientCertificate("bare");
        using X509Certificate2 bare = X509CertificateLoader.LoadCertificate(source.Export(X509ContentType.Cert));
        using X509Certificate2 kept = CreateClientCertificate("kept");
        using TitaniumHttpHandler handler = Handler();
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { bare, kept };
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/who")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.IsTrue(body.Contains("CN=kept", StringComparison.Ordinal), body);
        Assert.IsFalse(body.Contains("CN=bare", StringComparison.Ordinal), body);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11UsesTargetHostAsTheServerName()
    {
        TaskCompletionSource<string> serverName = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/hello", () => Results.Text("ok")),
            null,
            HttpProtocols.Http1,
            requestClientCertificate: false,
            observedServerName: serverName);
        using TitaniumHttpHandler handler = Handler();
        handler.SslOptions.TargetHost = "app.example";
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("ok", body);
        Assert.AreEqual("app.example", await serverName.Task);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11EmptyTargetHostSendsTheUriHost()
    {
        TaskCompletionSource<string> serverName = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/hello", () => Results.Text("ok")),
            null,
            HttpProtocols.Http1,
            requestClientCertificate: false,
            observedServerName: serverName);
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("ok", body);
        Assert.AreEqual("127.0.0.1", await serverName.Task);
        Assert.AreEqual(1, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11Tls12IsRefusedBeforeTheRequest()
    {
        int seen = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            app => app.MapGet("/hello", () =>
            {
                seen++;
                return Results.Text("ok");
            }),
            null,
            HttpProtocols.Http1);
        using TitaniumHttpHandler handler = Handler();
        handler.SslOptions.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.SendAsync(request));
        Assert.IsTrue(HasMessage(ex, "The remote party selected a TLS version below 1.3."), ex.ToString());
        Assert.AreEqual(0, seen);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3EmptyTargetHostSendsTheUriHost()
    {
        using X509Certificate2 certificate = CreateHttp3Certificate();
        TaskCompletionSource<string> serverName = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, hello, _) =>
            {
                serverName.TrySetResult(hello.ServerName);
                return ValueTask.FromResult(new ManagedQuicServerConnectionOptions
                {
                    AllowManagedStackUnderFips = true,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                    },
                });
            },
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + listener.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture) + "/hello")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancel.Token);
        ManagedQuicConnection server = await accepted;
        await ReplyAsync(server, cancel.Token);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync(cancel.Token));
        Assert.AreEqual("127.0.0.1", await serverName.Task);
        Assert.AreEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        await server.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11DecompressesGzip()
    {
        byte[] payload;
        using (MemoryStream encoded = new())
        {
            using (GZipStream gzip = new(encoded, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write("hello"u8);
            }

            payload = encoded.ToArray();
        }

        string? acceptEncoding = null;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            app => app.MapGet("/gzip", async context =>
            {
                acceptEncoding = context.Request.Headers.AcceptEncoding.ToString();
                context.Response.Headers.ContentEncoding = "gzip";
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength = payload.Length;
                await context.Response.Body.WriteAsync(payload);
            }),
            null,
            HttpProtocols.Http1);
        using TitaniumHttpHandler handler = Handler();
        handler.AutomaticDecompression = DecompressionMethods.GZip;
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/gzip")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("hello", body);
        Assert.IsTrue(acceptEncoding?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true, acceptEncoding);
        Assert.AreEqual(1, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11SendsAndStoresACookie()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/hello", (HttpContext context) =>
            {
                string incoming = context.Request.Cookies["session"] ?? "none";
                context.Response.Cookies.Append("next", "two");
                return Results.Text(incoming);
            }),
            null,
            HttpProtocols.Http1);
        Uri uri = new("https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        CookieContainer cookies = new();
        cookies.Add(uri, new Cookie("session", "one"));
        using TitaniumHttpHandler handler = Handler();
        handler.CookieContainer = cookies;
        handler.UseCookies = true;
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("one", body);
        CookieCollection stored = cookies.GetCookies(uri);
        Assert.AreEqual("one", stored["session"]?.Value);
        Assert.AreEqual("two", stored["next"]?.Value);
        Assert.AreEqual(1, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11UseCookiesFalseLeavesTheContainerUnused()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/hello", (HttpContext context) =>
            {
                string incoming = context.Request.Cookies["session"] ?? "none";
                context.Response.Cookies.Append("next", "two");
                return Results.Text(incoming);
            }),
            null,
            HttpProtocols.Http1);
        Uri uri = new("https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/hello");
        CookieContainer cookies = new();
        cookies.Add(uri, new Cookie("session", "one"));
        using TitaniumHttpHandler handler = Handler();
        handler.CookieContainer = cookies;
        handler.UseCookies = false;
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("none", body);
        CookieCollection stored = cookies.GetCookies(uri);
        Assert.AreEqual(1, stored.Count);
        Assert.AreEqual("one", stored["session"]?.Value);
        Assert.IsNull(stored["next"]);
        Assert.AreEqual(1, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
    }

    private static X509Certificate2 CreateClientCertificate() => CreateClientCertificate("client");

    private static X509Certificate2 CreateClientCertificate(string commonName)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=" + commonName, key, HashAlgorithmName.SHA256);
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http2OrLowerRetriesWhenTheServerRejectsTheScheme()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/scheme", (HttpRequest request) =>
                request.Protocol == "HTTP/2"
                    ? Results.StatusCode(StatusCodes.Status421MisdirectedRequest)
                    : Results.Text("downgraded"));
        });
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:" + server.Port.ToString() + "/scheme")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("downgraded", body);
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http11IsRememberedAfterTheServerSelectsIt()
    {
        int connections = 0;
        await using LoopbackServer server = await LoopbackServer.StartAsync(
            static app => app.MapGet("/scheme", () => Results.Text("http11")),
            () => Interlocked.Increment(ref connections));
        using TitaniumHttpHandler handler = Handler();
        using HttpClient client = new(handler, disposeHandler: false);
        string address = "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/scheme";
        using HttpRequestMessage first = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using HttpResponseMessage firstResponse = await client.SendAsync(first);
        Assert.AreEqual("http11", await firstResponse.Content.ReadAsStringAsync());
        using HttpRequestMessage second = new(HttpMethod.Get, address)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using HttpResponseMessage secondResponse = await client.SendAsync(second);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual("http11", await secondResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(2, connections);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Http3OrLowerFallsBackWhenTheHandshakeFails()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(static app =>
        {
            app.MapGet("/down", () => Results.Text("http11"));
        });
        using TitaniumHttpHandler handler = Handler();
        handler.ConnectTimeout = TimeSpan.FromMilliseconds(400);
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpRequestMessage request = new(HttpMethod.Get, "https://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/down")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        DateTime started = DateTime.UtcNow;
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("http11", body);
        Assert.AreNotEqual(3, response.Version.Major);
        Assert.AreSame(request, response.RequestMessage);
        Assert.AreEqual("https", response.RequestMessage!.RequestUri!.Scheme);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    private static HttpRequestMessage Request(string address) => new(HttpMethod.Get, address)
    {
        Version = HttpVersion.Version30,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };

    private static byte[] Pattern(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 17) + 3);
        }

        return data;
    }

    private static TitaniumHttpHandler Handler()
    {
        TitaniumHttpHandler handler = new()
        {
            AllowManagedStackUnderFips = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        return handler;
    }

    private static X509Certificate2 CreateHttp3Certificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }

    private static async Task SendQpackSettingsAsync(ManagedQuicConnection server, CancellationToken cancellationToken)
    {
        Http3Settings settings = new();
        settings.SetQpackMaxTableCapacity(4096);
        settings.SetQpackBlockedStreams(16);
        ManagedQuicStream control = await server.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        byte[] type = new byte[8];
        int typeLength = Http3VarInt.Write(type, Http3StreamType.Control);
        await control.WriteAsync(type.AsMemory(0, typeLength), cancellationToken);
        await WriteFrameAsync(control, Http3FrameType.Settings, settings.Serialize(), cancellationToken);
    }

    private static async Task ReadClientEncoderAsync(ManagedQuicConnection server, QpackContext table, CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < 3; i++)
            {
                ManagedQuicStream stream = await server.AcceptInboundUnidirectionalStreamAsync(cancellationToken);
                ulong? type = await Http3VarInt.ReadAsync(stream, cancellationToken);
                if (type == Http3StreamType.QpackEncoder)
                {
                    await QpackEncoderStreamReader.ProcessAsync(stream, table, cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException)
        {
        }
    }

    private static async Task<List<(string Name, string Value)>> DecodeWhenInsertedAsync(
        byte[] headers,
        QpackContext table,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                List<(string Name, string Value)> fields = QpackDecoder.Decode(headers, table, cancellationToken);
                if (fields.Exists(static field => field.Name == "x-row"))
                {
                    return fields;
                }
            }
            catch (Http3ConnectionException)
            {
            }

            await Task.Delay(20, cancellationToken);
        }

        return QpackDecoder.Decode(headers, table, cancellationToken);
    }

    private static async Task ReplyOnceAsync(ManagedQuicConnection server, CancellationToken cancellationToken)
    {
        ManagedQuicStream request = await server.AcceptInboundStreamAsync(cancellationToken);
        await ReplyOnStreamAsync(request, cancellationToken);
    }

    private static async Task ReplyOnStreamAsync(ManagedQuicStream request, CancellationToken cancellationToken)
    {
        byte[] block = QpackEncoder.Encode([(":status", "200")]);
        await WriteFrameAsync(request, Http3FrameType.Headers, block, cancellationToken);
        await WriteFrameAsync(request, Http3FrameType.Data, "ok"u8.ToArray(), cancellationToken);
        await request.CompleteWritesAsync(cancellationToken);
    }

    private static async Task<(string Headers, string Body)> ReadHttpRequestWithBodyAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, count);
            headerEnd = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8);
        }

        if (headerEnd < 0)
        {
            return (System.Text.Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), string.Empty);
        }

        string headers = System.Text.Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        int length = 0;
        foreach (string line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan(15).Trim(), out int parsed))
            {
                length = parsed;
            }
        }

        int have = (int)buffer.Length - (headerEnd + 4);
        while (have < length)
        {
            int count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, count);
            have += count;
        }

        string posted = System.Text.Encoding.ASCII.GetString(buffer.GetBuffer(), headerEnd + 4, Math.Min(length, have));
        return (headers, posted);
    }

    private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        while (true)
        {
            int count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, count);
            if (buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        return System.Text.Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task<ManagedQuicListener> ListenHttp3Async(X509Certificate2 certificate) =>
        await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                AllowManagedStackUnderFips = true,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") },
                },
            }),
        });

    private static async Task<(string Path, string Authorization)> ReadPathAndAuthorizationAsync(
        ManagedQuicStream request,
        CancellationToken cancellationToken)
    {
        byte[]? headers = await ReadFrameAsync(request, cancellationToken);
        Assert.IsNotNull(headers);
        while (await ReadFrameAsync(request, cancellationToken) is not null)
        {
        }

        string path = string.Empty;
        string authorization = string.Empty;
        foreach ((string name, string value) in QpackDecoder.Decode(headers))
        {
            if (name == ":path")
            {
                path = value;
            }
            else if (string.Equals(name, "authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorization = value;
            }
        }

        return (path, authorization);
    }

    private static async Task<string> ReadRedirectAuthorityAsync(
        ManagedQuicConnection server,
        string expected,
        CancellationToken cancellationToken)
    {
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancellationToken);
        (string path, string authority) = await ReadPathAndAuthorityAsync(hop, cancellationToken);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        Assert.AreEqual(expected, authority);
        await WriteRedirectAsync(hop, "302", "/land", cancellationToken);
        hop = await server.AcceptInboundStreamAsync(cancellationToken);
        (path, authority) = await ReadPathAndAuthorityAsync(hop, cancellationToken);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        Assert.AreEqual(expected, authority);
        await ReplyTextAsync(hop, authority, cancellationToken);
        return authority;
    }

    private static async Task<(string Path, string Authority)> ReadPathAndAuthorityAsync(
        ManagedQuicStream request,
        CancellationToken cancellationToken)
    {
        byte[]? headers = await ReadFrameAsync(request, cancellationToken);
        Assert.IsNotNull(headers);
        while (await ReadFrameAsync(request, cancellationToken) is not null)
        {
        }

        string path = string.Empty;
        string authority = string.Empty;
        foreach ((string name, string value) in QpackDecoder.Decode(headers))
        {
            if (name == ":path")
            {
                path = value;
            }
            else if (name == ":authority")
            {
                authority = value;
            }

            Assert.AreNotEqual("evil.example", value, name);
            Assert.AreNotEqual("host", name, value);
        }

        return (path, authority);
    }

    private static async Task<string> ReadRedirectCookieAsync(ManagedQuicConnection server, CancellationToken cancellationToken)
    {
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancellationToken);
        (string path, string cookie) = await ReadPathAndCookieAsync(hop, cancellationToken);
        Assert.IsTrue(path.EndsWith("/from", StringComparison.Ordinal));
        Assert.AreEqual("sid=manual", cookie);
        await WriteRedirectAsync(hop, "302", "/land", cancellationToken);
        hop = await server.AcceptInboundStreamAsync(cancellationToken);
        (path, cookie) = await ReadPathAndCookieAsync(hop, cancellationToken);
        Assert.IsTrue(path.EndsWith("/land", StringComparison.Ordinal));
        await ReplyTextAsync(hop, cookie.Length == 0 ? "none" : cookie, cancellationToken);
        return cookie;
    }

    private static async Task<(string Path, string Cookie)> ReadPathAndCookieAsync(
        ManagedQuicStream request,
        CancellationToken cancellationToken)
    {
        byte[]? headers = await ReadFrameAsync(request, cancellationToken);
        Assert.IsNotNull(headers);
        while (await ReadFrameAsync(request, cancellationToken) is not null)
        {
        }

        string path = string.Empty;
        string cookie = string.Empty;
        foreach ((string name, string value) in QpackDecoder.Decode(headers))
        {
            if (name == ":path")
            {
                path = value;
            }
            else if (string.Equals(name, "cookie", StringComparison.OrdinalIgnoreCase))
            {
                cookie = value;
            }
        }

        return (path, cookie);
    }

    private static async Task ServeRedirectChainAsync(ManagedQuicConnection server, bool finish, CancellationToken cancellationToken)
    {
        ManagedQuicStream hop = await server.AcceptInboundStreamAsync(cancellationToken);
        string path = await ReadRequestPathAsync(hop, cancellationToken);
        Assert.IsTrue(path.EndsWith("/a", StringComparison.Ordinal));
        await WriteRedirectAsync(hop, "302", "/b", cancellationToken);
        hop = await server.AcceptInboundStreamAsync(cancellationToken);
        path = await ReadRequestPathAsync(hop, cancellationToken);
        Assert.IsTrue(path.EndsWith("/b", StringComparison.Ordinal));
        await WriteRedirectAsync(hop, "302", "/c", cancellationToken);
        if (!finish)
        {
            return;
        }

        hop = await server.AcceptInboundStreamAsync(cancellationToken);
        path = await ReadRequestPathAsync(hop, cancellationToken);
        Assert.IsTrue(path.EndsWith("/c", StringComparison.Ordinal));
        await ReplyTextAsync(hop, "landed", cancellationToken);
    }

    private static async Task<(string Method, string Path, string Body)> ReadPostedRequestAsync(
        ManagedQuicStream request,
        CancellationToken cancellationToken)
    {
        byte[]? headers = await ReadFrameAsync(request, cancellationToken);
        Assert.IsNotNull(headers);
        using var body = new MemoryStream();
        while (await ReadFrameAsync(request, cancellationToken) is byte[] frame)
        {
            body.Write(frame);
        }

        string method = string.Empty;
        string path = string.Empty;
        foreach ((string name, string value) in QpackDecoder.Decode(headers))
        {
            if (name == ":method")
            {
                method = value;
            }
            else if (name == ":path")
            {
                path = value;
            }
        }

        return (method, path, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private static async Task WriteRedirectAsync(
        ManagedQuicStream stream,
        string status,
        string location,
        CancellationToken cancellationToken)
    {
        byte[] headers = QpackEncoder.Encode([(":status", status), ("location", location)]);
        await WriteFrameAsync(stream, Http3FrameType.Headers, headers, cancellationToken);
        await stream.CompleteWritesAsync(cancellationToken);
    }

    private static async Task ReplySawAsync(ManagedQuicStream stream, string saw, CancellationToken cancellationToken)
    {
        byte[] headers = QpackEncoder.Encode([(":status", "200"), ("x-saw", saw)]);
        await WriteFrameAsync(stream, Http3FrameType.Headers, headers, cancellationToken);
        await stream.CompleteWritesAsync(cancellationToken);
    }

    private static async Task ReplyTextAsync(ManagedQuicStream stream, string text, CancellationToken cancellationToken)
    {
        byte[] headers = QpackEncoder.Encode([(":status", "200")]);
        await WriteFrameAsync(stream, Http3FrameType.Headers, headers, cancellationToken);
        await WriteFrameAsync(stream, Http3FrameType.Data, System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);
        await stream.CompleteWritesAsync(cancellationToken);
    }

    private static async Task<string> ReadRequestPathAsync(ManagedQuicStream request, CancellationToken cancellationToken)
    {
        byte[]? headers = await ReadFrameAsync(request, cancellationToken);
        Assert.IsNotNull(headers);
        while (await ReadFrameAsync(request, cancellationToken) is not null)
        {
        }

        foreach ((string name, string value) in QpackDecoder.Decode(headers))
        {
            if (name == ":path")
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static async Task ReplyAsync(ManagedQuicConnection server, CancellationToken cancellationToken)
    {
        ManagedQuicStream request = await server.AcceptInboundStreamAsync(cancellationToken);
        await ReplyOnAcceptedAsync(request, cancellationToken);
    }

    private static async Task ReplyOnAcceptedAsync(ManagedQuicStream request, CancellationToken cancellationToken)
    {
        while (await ReadFrameAsync(request, cancellationToken) is not null)
        {
        }

        byte[] block = QpackEncoder.Encode([(":status", "200")]);
        await WriteFrameAsync(request, Http3FrameType.Headers, block, cancellationToken);
        await WriteFrameAsync(request, Http3FrameType.Data, "ok"u8.ToArray(), cancellationToken);
        await request.CompleteWritesAsync(cancellationToken);
    }

    private static async Task<ManagedQuicStream> HeadersAsync(ManagedQuicConnection server, CancellationToken cancellationToken)
    {
        ManagedQuicStream request = await server.AcceptInboundStreamAsync(cancellationToken);
        while (await ReadFrameAsync(request, cancellationToken) is not null)
        {
        }

        byte[] block = QpackEncoder.Encode([(":status", "200")]);
        await WriteFrameAsync(request, Http3FrameType.Headers, block, cancellationToken);
        return request;
    }

    private static async Task SendGoAwayAsync(ManagedQuicConnection server, ulong streamId, CancellationToken cancellationToken)
    {
        ManagedQuicStream control = await server.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        byte[] type = new byte[8];
        int typeLength = Http3VarInt.Write(type, Http3StreamType.Control);
        await control.WriteAsync(type.AsMemory(0, typeLength), cancellationToken);
        byte[] id = new byte[8];
        int idLength = Http3VarInt.Write(id, streamId);
        byte[] payload = new byte[idLength];
        id.AsSpan(0, idLength).CopyTo(payload);
        await WriteFrameAsync(control, Http3FrameType.GoAway, payload, cancellationToken);
    }

    private static async Task SendEncoderInsertAsync(ManagedQuicConnection server, string name, string value, CancellationToken cancellationToken)
    {
        ManagedQuicStream encoder = await server.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        byte[] type = new byte[8];
        int typeLength = Http3VarInt.Write(type, Http3StreamType.QpackEncoder);
        await encoder.WriteAsync(type.AsMemory(0, typeLength), cancellationToken);
        byte[] nameBytes = System.Text.Encoding.Latin1.GetBytes(name);
        byte[] valueBytes = System.Text.Encoding.Latin1.GetBytes(value);
        byte[] instruction = new byte[1 + 1 + nameBytes.Length + 1 + valueBytes.Length];
        instruction[0] = 0x20;
        instruction[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(instruction.AsSpan(2));
        instruction[2 + nameBytes.Length] = (byte)valueBytes.Length;
        valueBytes.CopyTo(instruction.AsSpan(3 + nameBytes.Length));
        await encoder.WriteAsync(instruction, cancellationToken);
    }

    private static async Task ReplyWithBlockAsync(ManagedQuicConnection server, byte[] block, CancellationToken cancellationToken)
    {
        ManagedQuicStream request = await server.AcceptInboundStreamAsync(cancellationToken);
        while (await ReadFrameAsync(request, cancellationToken) is not null)
        {
        }

        await WriteFrameAsync(request, Http3FrameType.Headers, block, cancellationToken);
        await WriteFrameAsync(request, Http3FrameType.Data, "ok"u8.ToArray(), cancellationToken);
        await request.CompleteWritesAsync(cancellationToken);
    }

    private static async Task WriteDownloadAsync(ManagedQuicStream stream, byte[] download, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < download.Length)
        {
            int count = Math.Min(16 * 1024, download.Length - offset);
            byte[] slice = new byte[count];
            download.AsSpan(offset, count).CopyTo(slice);
            await WriteFrameAsync(stream, Http3FrameType.Data, slice, cancellationToken);
            offset += count;
        }

        await stream.CompleteWritesAsync(cancellationToken);
    }

    private static async Task<(ulong Type, byte[] Payload)?> ReadTypedFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ulong? type = await Http3VarInt.ReadAsync(stream, cancellationToken);
        if (type is null)
        {
            return null;
        }

        ulong? length = await Http3VarInt.ReadAsync(stream, cancellationToken);
        if (length is null)
        {
            return null;
        }

        byte[] payload = new byte[(int)length.Value];
        int read = 0;
        while (read < payload.Length)
        {
            int count = await stream.ReadAsync(payload.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                return null;
            }

            read += count;
        }

        return (type.Value, payload);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ulong? type = await Http3VarInt.ReadAsync(stream, cancellationToken);
        if (type is null)
        {
            return null;
        }

        ulong? length = await Http3VarInt.ReadAsync(stream, cancellationToken);
        if (length is null)
        {
            return null;
        }

        byte[] payload = new byte[(int)length.Value];
        int read = 0;
        while (read < payload.Length)
        {
            int count = await stream.ReadAsync(payload.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                return null;
            }

            read += count;
        }

        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, ulong type, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[payload.Length + 16];
        int written = Http3FrameCodec.Write(frame, type, payload);
        await stream.WriteAsync(frame.AsMemory(0, written), cancellationToken);
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly X509Certificate2 _certificate;

        private LoopbackServer(WebApplication app, X509Certificate2 certificate, int port)
        {
            _app = app;
            _certificate = certificate;
            Port = port;
        }

        public int Port { get; }

        public static Task<LoopbackServer> StartAsync(Action<WebApplication> map) =>
            StartAsync(map, null, HttpProtocols.Http1AndHttp2);

        public static Task<LoopbackServer> StartAsync(Action<WebApplication> map, Action? onConnection) =>
            StartAsync(map, onConnection, HttpProtocols.Http1AndHttp2);

        public static Task<LoopbackServer> StartAsync(Action<WebApplication> map, Action? onConnection, HttpProtocols protocols) =>
            StartAsync(map, onConnection, protocols, requestClientCertificate: false);

        public static async Task<LoopbackServer> StartAsync(
            Action<WebApplication> map,
            Action? onConnection,
            HttpProtocols protocols,
            bool requestClientCertificate,
            TaskCompletionSource<string>? observedServerName = null)
        {
            X509Certificate2 certificate = CreateCertificate();
            try
            {
                WebApplicationBuilder builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0, listen =>
                    {
                        if (onConnection is not null)
                        {
                            listen.Use(next => connection =>
                            {
                                onConnection();
                                return next(connection);
                            });
                        }

                        listen.Protocols = protocols;
                        if (requestClientCertificate)
                        {
                            listen.UseHttps(adapter =>
                            {
                                adapter.ServerCertificate = certificate;
                                adapter.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                                adapter.ClientCertificateValidation = static (_, _, _) => true;
                            });
                        }
                        else if (observedServerName is not null)
                        {
                            listen.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.TlsHandshakeCallbackOptions
                            {
                                OnConnection = context =>
                                {
                                    observedServerName.TrySetResult(context.ClientHelloInfo.ServerName);
                                    List<SslApplicationProtocol> applicationProtocols = new();
                                    if ((protocols & HttpProtocols.Http2) != 0)
                                    {
                                        applicationProtocols.Add(SslApplicationProtocol.Http2);
                                    }

                                    if ((protocols & HttpProtocols.Http1) != 0)
                                    {
                                        applicationProtocols.Add(SslApplicationProtocol.Http11);
                                    }

                                    return new ValueTask<SslServerAuthenticationOptions>(new SslServerAuthenticationOptions
                                    {
                                        ServerCertificate = certificate,
                                        ApplicationProtocols = applicationProtocols,
                                    });
                                },
                            });
                        }
                        else
                        {
                            listen.UseHttps(certificate);
                        }
                    });
                });
                WebApplication app = builder.Build();
                map(app);
                await app.StartAsync();
                IServerAddressesFeature? addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                string? address = null;
                if (addresses is not null)
                {
                    foreach (string candidate in addresses.Addresses)
                    {
                        address = candidate;
                    }
                }

                if (address is null)
                {
                    await app.DisposeAsync();
                    throw new InvalidOperationException("Kestrel did not publish an address.");
                }

                return new LoopbackServer(app, certificate, new Uri(address).Port);
            }
            catch
            {
                certificate.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _certificate.Dispose();
        }

        private static X509Certificate2 CreateCertificate()
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256);
            using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(
                created.Export(X509ContentType.Pfx),
                password: null,
                X509KeyStorageFlags.Exportable);
        }
    }
}
