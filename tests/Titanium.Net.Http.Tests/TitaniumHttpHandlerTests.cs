#pragma warning disable CA1416 // QuicStreamType is the BCL name. These tests do not start the platform QUIC stack.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
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

    private static HttpRequestMessage Request(string address) => new(HttpMethod.Get, address)
    {
        Version = HttpVersion.Version30,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };

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

    private static async Task ReplyAsync(ManagedQuicConnection server, CancellationToken cancellationToken)
    {
        ManagedQuicStream request = await server.AcceptInboundStreamAsync(cancellationToken);
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

        public static async Task<LoopbackServer> StartAsync(Action<WebApplication> map)
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
                        listen.Protocols = HttpProtocols.Http1AndHttp2;
                        listen.UseHttps(certificate);
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
