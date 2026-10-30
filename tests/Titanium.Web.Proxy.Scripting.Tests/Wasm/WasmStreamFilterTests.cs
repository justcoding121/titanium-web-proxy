using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Scripting.Wasm;
using static Titanium.Web.Proxy.Scripting.Tests.Wasm.WasmTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

[TestClass]
[DoNotParallelize]
public class WasmStreamFilterTests
{
    [TestMethod]
    public async Task Stream_HoldsUntilTheGuestContinues_RewritesBothDirections_AndClosesTheContext()
    {
        var logger = new ListLogger();
        using var plugin = Create(Config("tcp_filter.wat"), logger);
        IStreamScriptFilter filter = plugin;
        var connection = Connection();

        Assert.AreEqual(ScriptAction.Continue, (await filter.OnNewConnectionAsync(connection, default)).Action);
        var held = await filter.OnDownstreamDataAsync(connection, Ascii("+ab"), default);
        var released = await filter.OnDownstreamDataAsync(connection, Ascii("c."), default);
        var plain = await filter.OnDownstreamDataAsync(connection, Ascii("ping"), default);
        var upstream = await filter.OnUpstreamDataAsync(connection, Ascii("A:pong"), default);
        await filter.OnCloseAsync(connection);

        Assert.AreEqual(0, held.ReplaceData!.Length, "a held piece is not forwarded");
        Assert.AreEqual("abc.", Encoding.ASCII.GetString(released.ReplaceData!), "the held piece comes first, without the '+'");
        Assert.IsNull(plain.ReplaceData, "an untouched piece is forwarded as read");
        Assert.AreEqual("wasm|A:pong", Encoding.ASCII.GetString(upstream.ReplaceData!));
        Assert.AreEqual(0, connection.Items.Count);
        Assert.IsTrue(logger.Lines.Any(l => l.EndsWith("[wasm test] closed", StringComparison.Ordinal)));
        Assert.IsTrue(logger.Lines.Any(l => l.EndsWith("[wasm test] stream-log", StringComparison.Ordinal)));
        Assert.AreEqual(1, plugin.Pool.Vms[0]!.Contexts.Count(), "only the root context is left");
    }

    [TestMethod]
    public async Task Stream_CloseStream_Denies_AtAcceptOrOnData()
    {
        using var denier = Create(Config("tcp_filter.wat", settings: new Dictionary<string, string> { ["configuration"] = "deny" }));
        using var plugin = Create(Config("tcp_filter.wat", name: "plain"));
        IStreamScriptFilter filter = plugin;
        var connection = Connection();

        Assert.AreEqual(ScriptAction.Deny, (await ((IStreamScriptFilter)denier).OnNewConnectionAsync(Connection(), default)).Action);
        await filter.OnNewConnectionAsync(connection, default);
        Assert.AreEqual(ScriptAction.Deny, (await filter.OnDownstreamDataAsync(connection, Ascii("!stop"), default)).Action);
        Assert.AreEqual(ScriptAction.Deny, (await filter.OnDownstreamDataAsync(connection, Ascii("more"), default)).Action, "a closed stream stays closed");
    }

    [TestMethod]
    public async Task Stream_CanChooseTheCluster_AndReadsConnectionProperties()
    {
        using var plugin = Create(Config("tcp_filter.wat", settings: new Dictionary<string, string> { ["configuration"] = "route-b" }));
        var connection = Connection();

        var result = await ((IStreamScriptFilter)plugin).OnNewConnectionAsync(connection, default);

        Assert.AreEqual("b", result.ClusterId);
        var stream = (WasmStreamContext)connection.Items.Values.Single()!;
        Assert.AreEqual("sni.test", Encoding.UTF8.GetString(WasmProperties.Get(stream, "connection.requested_server_name")!));
        Assert.AreEqual("127.0.0.1:5555", Encoding.UTF8.GetString(WasmProperties.Get(stream, "source.address")!));
        Assert.AreEqual("b", Encoding.UTF8.GetString(WasmProperties.Get(stream, "cluster_name")!));
        await ((IStreamScriptFilter)plugin).OnCloseAsync(connection);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Passthrough_RunsTheWasmStreamFilter_EndToEnd()
    {
        using var plugin = Create(Config("tcp_filter.wat"));
        await using var origin = new EchoTcpOrigin("A");
        await using var rig = await PassthroughRig.StartAsync(origin.Port, [plugin]);

        using var socket = await rig.ConnectAsync();
        Assert.AreEqual("wasm|A:ping", await RoundTripAsync(socket, "ping", "wasm|A:ping".Length));

        await socket.SendAsync(Encoding.ASCII.GetBytes("+ab"));
        await Task.Delay(100);
        Assert.AreEqual("wasm|A:abc.", await RoundTripAsync(socket, "c.", "wasm|A:abc.".Length));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Passthrough_DeniedByTheGuest_NeverReachesTheUpstream()
    {
        using var plugin = Create(Config("tcp_filter.wat", settings: new Dictionary<string, string> { ["configuration"] = "deny" }));
        await using var origin = new EchoTcpOrigin("A");
        await using var rig = await PassthroughRig.StartAsync(origin.Port, [plugin]);

        using var socket = await rig.ConnectAsync();
        int read;
        try
        {
            await socket.SendAsync(Encoding.ASCII.GetBytes("x"));
            read = await socket.ReceiveAsync(new byte[8]);
        }
        catch (SocketException)
        {
            read = 0;
        }

        Assert.AreEqual(0, read);
        Assert.AreEqual(0, origin.Accepted);
    }

    private static StreamConnectionContext Connection() => new()
    {
        ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 5555),
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
        ServerName = "sni.test",
        ClusterId = "a",
        Items = new Dictionary<string, object?>(),
    };

    private static ReadOnlyMemory<byte> Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static async Task<string> RoundTripAsync(Socket socket, string text, int expected)
    {
        await socket.SendAsync(Encoding.ASCII.GetBytes(text));
        var buffer = new byte[256];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (total < expected)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(total), cts.Token);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    /// <summary>A passthrough listener forwarding to one port, with the given L4 filters.</summary>
    private sealed class PassthroughRig : IAsyncDisposable
    {
        private readonly ProxyServer _proxy;
        private readonly TransparentProxyEndPoint _endPoint;
        private readonly ClusterManager _manager;

        private PassthroughRig(ProxyServer proxy, TransparentProxyEndPoint endPoint, ClusterManager manager)
        {
            _proxy = proxy;
            _endPoint = endPoint;
            _manager = manager;
        }

        public static async Task<PassthroughRig> StartAsync(int forwardPort, IReadOnlyList<IStreamScriptFilter> filters)
        {
            var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
            var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
            {
                Passthrough = true,
                ForwardHost = "127.0.0.1",
                ForwardPort = forwardPort,
                StreamFilters = filters,
            };
            proxy.AddEndPoint(endPoint);
            var manager = new ClusterManager();
            var balancer = new LoadBalancer();
            manager.AttachHealthTracker(balancer.Health);
            await manager.ApplyAsync(Array.Empty<ClusterConfig>());
            proxy.ReverseProxy = new ReverseProxyOptions
            {
                ClusterManager = manager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = balancer,
            };
            proxy.Start();
            return new PassthroughRig(proxy, endPoint, manager);
        }

        public async Task<Socket> ConnectAsync()
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, _endPoint.Port);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            _proxy.Stop();
            _proxy.Dispose();
            await _manager.DisposeAsync();
        }
    }

    /// <summary>Echoes each read back with its name as a prefix, and counts accepted connections.</summary>
    private sealed class EchoTcpOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _name;
        private int _accepted;

        public EchoTcpOrigin(string name)
        {
            _name = name;
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            return default;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => EchoAsync(socket));
            }
        }

        private async Task EchoAsync(Socket socket)
        {
            using (socket)
            {
                var buffer = new byte[256];
                try
                {
                    while (true)
                    {
                        var n = await socket.ReceiveAsync(buffer, _stop.Token);
                        if (n == 0)
                        {
                            return;
                        }

                        await socket.SendAsync(Encoding.ASCII.GetBytes(_name + ":" + Encoding.ASCII.GetString(buffer, 0, n)), _stop.Token);
                    }
                }
                catch
                {
                    // The peer or the test ended the connection.
                }
            }
        }
    }
}
