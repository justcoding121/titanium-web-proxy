using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Titanium.Gateway.Kube;

/// <summary>
///     A list-then-watch cache of one resource collection. It keeps the parsed objects, relists when the API
///     server says the resource version is too old (410), and backs off after failures. Everything it reads stays
///     in memory; nothing is written to disk.
/// </summary>
public sealed class Informer<T> where T : class
{
    private readonly IKubeApi _api;
    private readonly string _path;
    private readonly string? _fieldSelector;
    private readonly Func<JsonElement, T?> _parse;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);
    private IReadOnlyList<T>? _snapshot = [];

    public Informer(IKubeApi api, string path, string? fieldSelector, Func<JsonElement, T?> parse, ILogger log, TimeProvider? time = null)
    {
        _api = api;
        _path = path;
        _fieldSelector = fieldSelector;
        _parse = parse;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public string Path => _path;

    /// <summary>True after the first successful list. Stays true across later relists.</summary>
    public bool HasSynced { get; private set; }

    /// <summary>Raised after the store changed. Runs on the informer's thread; keep handlers short.</summary>
    public event Action? Changed;

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_gate)
            {
                return _snapshot ??= _items.Values.ToList();
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var resourceVersion = await ListAsync(cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);
                await WatchAsync(resourceVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Informer {Path} failed: {Message}. Retrying in {Seconds}s.", _path, ex.Message, (int)backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private string Url(string query)
    {
        var selector = _fieldSelector is null ? string.Empty : "&fieldSelector=" + Uri.EscapeDataString(_fieldSelector);
        return $"{_path}?{query}{selector}";
    }

    private async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        var fresh = new Dictionary<string, T>(StringComparer.Ordinal);
        string? continueToken = null;
        string resourceVersion;
        do
        {
            var query = "limit=500" + (continueToken is null ? string.Empty : "&continue=" + Uri.EscapeDataString(continueToken));
            var response = await _api.SendAsync(HttpMethod.Get, Url(query), null, null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                throw new KubeApiException(response.Status, response.Body);
            }

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            foreach (var item in root.GetArray("items"))
            {
                Store(fresh, item.Clone());
            }

            resourceVersion = root.GetString("metadata", "resourceVersion") ?? string.Empty;
            var next = root.GetString("metadata", "continue");
            continueToken = string.IsNullOrEmpty(next) ? null : next;
        }
        while (continueToken is not null);

        lock (_gate)
        {
            _items.Clear();
            foreach (var pair in fresh)
            {
                _items[pair.Key] = pair.Value;
            }

            _snapshot = null;
        }

        HasSynced = true;
        Changed?.Invoke();
        return resourceVersion;
    }

    private void Store(Dictionary<string, T> target, JsonElement element)
    {
        var key = KeyOf(element);
        if (key is null)
        {
            return;
        }

        T? parsed;
        try
        {
            parsed = _parse(element);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _log.LogWarning("Ignoring {Key} in {Path}: {Message}", key, _path, ex.Message);
            target.Remove(key);
            return;
        }

        if (parsed is null)
        {
            target.Remove(key);
        }
        else
        {
            target[key] = parsed;
        }
    }

    private async Task WatchAsync(string resourceVersion, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = _time.GetTimestamp();
            var query = $"watch=true&allowWatchBookmarks=true&timeoutSeconds=270&resourceVersion={Uri.EscapeDataString(resourceVersion)}";
            using var stream = await _api.OpenWatchAsync(Url(query), cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetString("type");
                var obj = root.Get("object");
                if (obj is null)
                {
                    continue;
                }

                switch (type)
                {
                    case "ADDED" or "MODIFIED":
                        lock (_gate)
                        {
                            Store(_items, obj.Value.Clone());
                            _snapshot = null;
                        }

                        resourceVersion = obj.Value.GetString("metadata", "resourceVersion") ?? resourceVersion;
                        Changed?.Invoke();
                        break;
                    case "DELETED":
                        lock (_gate)
                        {
                            var key = KeyOf(obj.Value);
                            if (key is not null)
                            {
                                _items.Remove(key);
                            }

                            _snapshot = null;
                        }

                        resourceVersion = obj.Value.GetString("metadata", "resourceVersion") ?? resourceVersion;
                        Changed?.Invoke();
                        break;
                    case "BOOKMARK":
                        resourceVersion = obj.Value.GetString("metadata", "resourceVersion") ?? resourceVersion;
                        break;
                    case "ERROR":
                        var code = obj.Value.GetInt("code") ?? 500;
                        throw new KubeApiException((HttpStatusCode)code, obj.Value.GetString("message") ?? line);
                }
            }

            // The server ended the watch (timeoutSeconds). Reopen from the last version, but do not spin if it
            // keeps ending at once.
            if (_time.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string? KeyOf(JsonElement element)
    {
        var name = element.GetString("metadata", "name");
        if (name is null)
        {
            return null;
        }

        var ns = element.GetString("metadata", "namespace");
        return ns is null ? name : ns + "/" + name;
    }
}
