using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Tests.Support;

/// <summary>One write the controller made.</summary>
public sealed record RecordedRequest(HttpMethod Method, string Path, string Query, string? Body, string? ContentType);

/// <summary>
///     An in-memory Kubernetes API server behind <see cref="IKubeApi"/>: list, get, create, replace, merge patch,
///     server-side apply (as an upsert merge), resourceVersion conflicts, and watch streams with replay. It parses
///     real REST paths, so the informers, the leader election and the writers run unchanged against it.
/// </summary>
public sealed class FakeKubeApi : IKubeApi
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonObject> _objects = new(StringComparer.Ordinal);
    private readonly List<(long Version, string Plural, string Type, JsonObject Object)> _events = [];
    private readonly List<Watcher> _watchers = [];
    private long _version = 100;

    /// <summary>Every non-GET request, in order.</summary>
    public List<RecordedRequest> Writes { get; } = [];

    /// <summary>When true every request fails like a lost connection.</summary>
    public bool Offline { get; set; }

    /// <summary>The next watch after this version answers 410 once, forcing a relist.</summary>
    public bool ExpireNextWatch { get; set; }

    public int ListCount { get; private set; }

    public int WatchCount { get; private set; }

    public JsonObject? Get(string plural, string? ns, string name)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(Key(plural, ns, name), out var value) ? (JsonObject)value.DeepClone() : null;
        }
    }

    public IReadOnlyList<JsonObject> List(string plural) =>
        Snapshot(plural, null);

    public IReadOnlyList<RecordedRequest> WritesTo(string pathPart) =>
        Writes.Where(w => w.Path.Contains(pathPart, StringComparison.Ordinal)).ToList();

    /// <summary>Adds or replaces an object, as if a user applied it, and tells every watcher.</summary>
    public void Apply(string plural, JsonObject obj)
    {
        lock (_gate)
        {
            var meta = (JsonObject)(obj["metadata"] ??= new JsonObject());
            var ns = meta["namespace"]?.GetValue<string>();
            var name = meta["name"]!.GetValue<string>();
            var key = Key(plural, ns, name);
            var existed = _objects.ContainsKey(key);
            meta["resourceVersion"] = Next().ToString();
            meta["generation"] ??= 1;
            _objects[key] = (JsonObject)obj.DeepClone();
            Record(plural, existed ? "MODIFIED" : "ADDED", _objects[key]);
        }
    }

    public void Delete(string plural, string? ns, string name)
    {
        lock (_gate)
        {
            if (_objects.Remove(Key(plural, ns, name), out var removed))
            {
                removed["metadata"]!["resourceVersion"] = Next().ToString();
                Record(plural, "DELETED", removed);
            }
        }
    }

    public Task<KubeResponse> SendAsync(HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("fake API offline");
        }

        var (path, query) = Split(pathAndQuery);
        var route = Route.Parse(path);
        lock (_gate)
        {
            if (method != HttpMethod.Get)
            {
                Writes.Add(new RecordedRequest(method, path, query, body, contentType));
            }

            if (method == HttpMethod.Get)
            {
                return Task.FromResult(route.Name is null ? ListResponse(route, query) : GetResponse(route));
            }

            if (method == HttpMethod.Post)
            {
                return Task.FromResult(Create(route, body!));
            }

            if (method == HttpMethod.Put)
            {
                return Task.FromResult(Replace(route, body!));
            }

            if (method == HttpMethod.Patch)
            {
                return Task.FromResult(Patch(route, body!, contentType));
            }
        }

        return Task.FromResult(new KubeResponse(HttpStatusCode.MethodNotAllowed, string.Empty));
    }

    public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("fake API offline");
        }

        var (path, query) = Split(pathAndQuery);
        var route = Route.Parse(path);
        var since = long.Parse(Param(query, "resourceVersion") ?? "0");
        var selector = Param(query, "fieldSelector");
        var watcher = new Watcher(route, selector);
        lock (_gate)
        {
            WatchCount++;
            if (ExpireNextWatch)
            {
                ExpireNextWatch = false;
                watcher.Channel.Writer.TryWrite("{\"type\":\"ERROR\",\"object\":{\"kind\":\"Status\",\"code\":410,\"message\":\"too old resource version\"}}");
                watcher.Channel.Writer.TryComplete();
            }
            else
            {
                foreach (var e in _events.Where(e => e.Version > since && e.Plural == route.Plural))
                {
                    watcher.Offer(e.Type, e.Object);
                }

                _watchers.Add(watcher);
            }
        }

        cancellationToken.Register(() => watcher.Channel.Writer.TryComplete());
        return Task.FromResult<Stream>(new LineStream(watcher.Channel.Reader));
    }

    private static string Key(string plural, string? ns, string name) => $"{plural}|{ns}|{name}";

    private long Next() => ++_version;

    private void Record(string plural, string type, JsonObject obj)
    {
        var version = long.Parse(obj["metadata"]!["resourceVersion"]!.GetValue<string>());
        var copy = (JsonObject)obj.DeepClone();
        _events.Add((version, plural, type, copy));
        foreach (var watcher in _watchers.Where(w => w.Route.Plural == plural))
        {
            watcher.Offer(type, copy);
        }
    }

    private List<JsonObject> Snapshot(string plural, string? selector)
    {
        lock (_gate)
        {
            return _objects
                .Where(p => p.Key.StartsWith(plural + "|", StringComparison.Ordinal))
                .Where(p => Matches(p.Value, selector))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => (JsonObject)p.Value.DeepClone())
                .ToList();
        }
    }

    internal static bool Matches(JsonObject obj, string? selector)
    {
        if (string.IsNullOrEmpty(selector))
        {
            return true;
        }

        var eq = selector.IndexOf('=', StringComparison.Ordinal);
        return obj[selector[..eq]]?.GetValue<string>() == selector[(eq + 1)..];
    }

    private KubeResponse ListResponse(Route route, string query)
    {
        ListCount++;
        var items = Snapshot(route.Plural, Param(query, "fieldSelector"))
            .Where(o => route.Namespace is null || o["metadata"]!["namespace"]?.GetValue<string>() == route.Namespace)
            .ToList();
        var list = new JsonObject
        {
            ["metadata"] = new JsonObject { ["resourceVersion"] = _version.ToString() },
            ["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()),
        };
        return new KubeResponse(HttpStatusCode.OK, list.ToJsonString());
    }

    private KubeResponse GetResponse(Route route) =>
        _objects.TryGetValue(Key(route.Plural, route.Namespace, route.Name!), out var value)
            ? new KubeResponse(HttpStatusCode.OK, value.ToJsonString())
            : new KubeResponse(HttpStatusCode.NotFound, "{\"kind\":\"Status\",\"code\":404}");

    private KubeResponse Create(Route route, string body)
    {
        var obj = JsonNode.Parse(body)!.AsObject();
        var meta = obj["metadata"]!.AsObject();
        var key = Key(route.Plural, route.Namespace, meta["name"]!.GetValue<string>());
        if (_objects.ContainsKey(key))
        {
            return new KubeResponse(HttpStatusCode.Conflict, "{\"code\":409}");
        }

        meta["resourceVersion"] = Next().ToString();
        _objects[key] = obj;
        Record(route.Plural, "ADDED", obj);
        return new KubeResponse(HttpStatusCode.Created, obj.ToJsonString());
    }

    private KubeResponse Replace(Route route, string body)
    {
        var key = Key(route.Plural, route.Namespace, route.Name!);
        if (!_objects.TryGetValue(key, out var existing))
        {
            return new KubeResponse(HttpStatusCode.NotFound, "{\"code\":404}");
        }

        var obj = JsonNode.Parse(body)!.AsObject();
        var sent = obj["metadata"]?["resourceVersion"]?.GetValue<string>();
        if (sent is not null && sent != existing["metadata"]!["resourceVersion"]!.GetValue<string>())
        {
            return new KubeResponse(HttpStatusCode.Conflict, "{\"code\":409}");
        }

        obj["metadata"]!["resourceVersion"] = Next().ToString();
        _objects[key] = obj;
        Record(route.Plural, "MODIFIED", obj);
        return new KubeResponse(HttpStatusCode.OK, obj.ToJsonString());
    }

    private KubeResponse Patch(Route route, string body, string? contentType)
    {
        var key = Key(route.Plural, route.Namespace, route.Name!);
        var patch = JsonNode.Parse(body)!.AsObject();
        var exists = _objects.TryGetValue(key, out var existing);
        var apply = contentType?.Contains("apply-patch", StringComparison.Ordinal) == true;
        if (!exists && !apply)
        {
            return new KubeResponse(HttpStatusCode.NotFound, "{\"code\":404}");
        }

        var target = existing ?? new JsonObject();
        var sentVersion = patch["metadata"]?["resourceVersion"]?.GetValue<string>();
        if (sentVersion is not null && exists && sentVersion != target["metadata"]!["resourceVersion"]!.GetValue<string>())
        {
            return new KubeResponse(HttpStatusCode.Conflict, "{\"code\":409}");
        }

        // Replacing arrays wholesale and merging objects is RFC 7386, which is also what apply amounts to for the
        // objects this controller writes.
        Merge(target, patch);
        if (apply && !exists)
        {
            target["metadata"]!["name"] = route.Name;
            if (route.Namespace is not null)
            {
                target["metadata"]!["namespace"] = route.Namespace;
            }
        }

        target["metadata"]!["resourceVersion"] = Next().ToString();
        _objects[key] = target;
        Record(route.Plural, exists ? "MODIFIED" : "ADDED", target);
        return new KubeResponse(HttpStatusCode.OK, target.ToJsonString());
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch.ToList())
        {
            if (value is null)
            {
                target.Remove(name);
            }
            else if (value is JsonObject child && target[name] is JsonObject current)
            {
                Merge(current, child);
            }
            else
            {
                target[name] = value.DeepClone();
            }
        }
    }

    private static (string Path, string Query) Split(string pathAndQuery)
    {
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? (pathAndQuery, string.Empty) : (pathAndQuery[..q], pathAndQuery[(q + 1)..]);
    }

    private static string? Param(string query, string name)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && part[..eq] == name)
            {
                return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return null;
    }

    /// <summary>A parsed REST path: <c>[namespaces/{ns}/]{plural}[/{name}[/{sub}]]</c> after the API prefix.</summary>
    internal sealed record Route(string Plural, string? Namespace, string? Name, string? Sub)
    {
        public static Route Parse(string path)
        {
            var segments = path.Trim('/').Split('/');
            var rest = segments[0] == "api" ? segments[2..] : segments[3..];
            if (rest[0] == "namespaces" && rest.Length >= 3)
            {
                return new Route(rest[2], Uri.UnescapeDataString(rest[1]), rest.Length > 3 ? Uri.UnescapeDataString(rest[3]) : null, rest.Length > 4 ? rest[4] : null);
            }

            return new Route(rest[0], null, rest.Length > 1 ? Uri.UnescapeDataString(rest[1]) : null, rest.Length > 2 ? rest[2] : null);
        }
    }

    private sealed class Watcher
    {
        private readonly string? _selector;

        public Watcher(Route route, string? selector)
        {
            Route = route;
            _selector = selector;
        }

        public Route Route { get; }

        public Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public void Offer(string type, JsonObject obj)
        {
            if (Route.Namespace is not null && obj["metadata"]!["namespace"]?.GetValue<string>() != Route.Namespace)
            {
                return;
            }

            if (!Matches(obj, _selector))
            {
                return;
            }

            Channel.Writer.TryWrite(new JsonObject { ["type"] = type, ["object"] = obj.DeepClone() }.ToJsonString());
        }
    }

    /// <summary>Turns a channel of JSON lines into a byte stream, one line per event.</summary>
    private sealed class LineStream : Stream
    {
        private readonly ChannelReader<string> _reader;
        private byte[] _pending = [];
        private int _offset;

        public LineStream(ChannelReader<string> reader) => _reader = reader;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _pending.Length)
            {
                try
                {
                    if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !_reader.TryRead(out var line))
                    {
                        return 0;
                    }

                    _pending = Encoding.UTF8.GetBytes(line + "\n");
                    _offset = 0;
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
