using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Titanium.Cli.Admin;

/// <summary>Facts served by <c>GET /version</c>.</summary>
internal sealed record AdminVersionInfo(string Version, string Commit, string Runtime, string Edition, string ConfigHash);

/// <summary>
///     Admin listener: <c>/healthz</c>, <c>/readyz</c>, <c>/metrics</c>, <c>/version</c> on its own port.
///     <para>
///         It never shares a socket with a proxy listener, and the proxy request path never calls into it.
///         One request per connection, no keep-alive, request line and headers only.
///     </para>
///     <para>
///         <c>/healthz</c> and <c>/readyz</c> are unauthenticated so a kubelet can probe them.
///         <c>/metrics</c> and <c>/version</c> require <c>Authorization: Bearer &lt;token&gt;</c> when a token is
///         configured. The comparison is constant time over fixed-length digests.
///     </para>
/// </summary>
internal sealed class AdminServer : IDisposable
{
    public const string DefaultHost = "127.0.0.1";

    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] EmptyDigest = new byte[SHA256.HashSizeInBytes];

    private readonly TcpListener _listener;
    private readonly IReadinessProbe _readiness;
    private readonly MetricsExposition _exposition;
    private readonly Func<AdminVersionInfo> _version;
    private readonly CancellationTokenSource _stop = new();
    private byte[]? _tokenDigest;
    private Task? _loop;

    public AdminServer(
        IPAddress address,
        int port,
        IReadinessProbe readiness,
        MetricsExposition exposition,
        Func<AdminVersionInfo> version,
        string? authToken)
    {
        _listener = new TcpListener(address, port);
        _readiness = readiness;
        _exposition = exposition;
        _version = version;
        SetAuthToken(authToken);
    }

    /// <summary>The bound port. Meaningful after <see cref="Start" />.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>True when <c>/metrics</c> and <c>/version</c> require a bearer token.</summary>
    public bool RequiresToken => Volatile.Read(ref _tokenDigest) is not null;

    /// <summary>Replaces the bearer token (a reloaded Secret). Null or empty turns the check off.</summary>
    public void SetAuthToken(string? token) =>
        Volatile.Write(ref _tokenDigest, string.IsNullOrEmpty(token) ? null : Digest(token));

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The accept loop ends with an exception once the listener stops.
        }

        _stop.Dispose();
    }

    internal static bool TokenMatches(byte[] expectedDigest, string? supplied)
    {
        // Hash both sides so the comparison length never depends on the secret, and always compare.
        var actual = supplied is null ? EmptyDigest : Digest(supplied);
        return CryptographicOperations.FixedTimeEquals(expectedDigest, actual) && supplied is not null;
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                var response = Respond(head);
                var bytes = Encoding.UTF8.GetBytes(response.Body);
                var header = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(response.Status).Append("\r\n")
                    .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
                    .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
                    .Append("Cache-Control: no-store\r\n");
                if (response.Challenge)
                {
                    header.Append("WWW-Authenticate: Bearer\r\n");
                }

                header.Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), timeout.Token).ConfigureAwait(false);
                if (response.IncludeBody)
                {
                    await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A prober or scraper that goes away mid-request is not an error.
            }
        }
    }

    private AdminResponse Respond(string head)
    {
        var lines = head.Split("\r\n", StringSplitOptions.None);
        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
        {
            return AdminResponse.Text("400 Bad Request", "bad request\n");
        }

        var isHead = string.Equals(parts[0], "HEAD", StringComparison.Ordinal);
        if (!isHead && !string.Equals(parts[0], "GET", StringComparison.Ordinal))
        {
            return AdminResponse.Text("405 Method Not Allowed", "method not allowed\n");
        }

        var path = parts[1];
        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        var response = path switch
        {
            "/healthz" => AdminResponse.Text("200 OK", "ok\n"),
            "/readyz" => ReadyResponse(),
            "/metrics" => Authorize(lines) ?? new AdminResponse("200 OK", PrometheusTextWriter.ContentType, _exposition.Render()),
            "/version" => Authorize(lines) ?? new AdminResponse("200 OK", "application/json", VersionJson(_version())),
            _ => AdminResponse.Text("404 Not Found", "not found\n"),
        };

        return isHead ? response with { IncludeBody = false } : response;
    }

    private AdminResponse ReadyResponse()
    {
        var report = _readiness.Check();
        return report.Ready
            ? new AdminResponse("200 OK", "application/json", "{\"status\":\"ready\"}\n")
            : new AdminResponse("503 Service Unavailable", "application/json", NotReadyJson(report.Reasons));
    }

    private AdminResponse? Authorize(string[] lines)
    {
        var expected = Volatile.Read(ref _tokenDigest);
        if (expected is null)
        {
            return null;
        }

        string? supplied = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !lines[i].AsSpan(0, colon).Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i][(colon + 1)..].Trim();
            const string prefix = "Bearer ";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                supplied = value[prefix.Length..].Trim();
            }

            break;
        }

        return TokenMatches(expected, supplied)
            ? null
            : AdminResponse.Text("401 Unauthorized", "unauthorized\n") with { Challenge = true };
    }

    internal static string NotReadyJson(IReadOnlyList<string> reasons)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "not_ready");
            writer.WriteStartArray("reasons");
            foreach (var reason in reasons)
            {
                writer.WriteStringValue(reason);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    internal static string VersionJson(AdminVersionInfo info)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", info.Version);
            writer.WriteString("commit", info.Commit);
            writer.WriteString("runtime", info.Runtime);
            writer.WriteString("edition", info.Edition);
            writer.WriteString("config_hash", info.ConfigHash);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        var scanned = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            // Re-scan only the new bytes plus enough overlap to catch a terminator split across reads.
            var from = Math.Max(0, scanned - 3);
            if (buffer.AsSpan(from, length - from).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }

            scanned = length;
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    private sealed record AdminResponse(string Status, string ContentType, string Body)
    {
        public bool IncludeBody { get; init; } = true;

        public bool Challenge { get; init; }

        public static AdminResponse Text(string status, string body) => new(status, "text/plain; charset=utf-8", body);
    }
}
