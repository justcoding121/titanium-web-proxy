using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Control;

/// <summary>
///     Posts the Warning events the translator raised for Ingresses (a rejected rule, an unknown annotation). A
///     reconcile runs often and sees the same problem each time, so an event is posted once and again only after
///     <see cref="RepeatAfter"/>, as a bump of <c>count</c> and <c>lastTimestamp</c>, which keeps it visible past the
///     hour the API server keeps events for. A problem that goes away and comes back is posted again at once.
/// </summary>
public sealed class EventWriter
{
    /// <summary>The longest message an Event may carry is 1 KiB.</summary>
    private const int MaxMessage = 1000;

    private readonly IKubeApi _api;
    private readonly string _component;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (DateTimeOffset Posted, int Count)> _posted = new(StringComparer.Ordinal);

    public EventWriter(IKubeApi api, string component, ILogger log, TimeProvider? time = null)
    {
        _api = api;
        _component = component;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long a posted event stays quiet before the controller bumps it.</summary>
    public TimeSpan RepeatAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Posts the events that are new (or due for a bump). Returns how many requests were sent.</summary>
    public async Task<int> ApplyAsync(IReadOnlyList<IngressEvent> events, CancellationToken cancellationToken)
    {
        var current = events.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _posted.Keys.Where(k => !current.Contains(k)).ToList())
        {
            _posted.Remove(gone);
        }

        var sent = 0;
        var now = _time.GetUtcNow();
        foreach (var item in events)
        {
            var key = item.Key;
            var count = 1;
            if (_posted.TryGetValue(key, out var before))
            {
                if (now - before.Posted < RepeatAfter)
                {
                    continue;
                }

                count = before.Count + 1;
            }

            try
            {
                sent++;
                await PostAsync(item, count, now, cancellationToken).ConfigureAwait(false);
                _posted[key] = (now, count);
            }
            catch (Exception ex) when (ex is HttpRequestException or KubeApiException or IOException)
            {
                _log.LogWarning(ex, "Could not post an event for Ingress {Namespace}/{Name}: {Message}", item.Namespace, item.Name, ex.Message);
            }
        }

        return sent;
    }

    private async Task PostAsync(IngressEvent item, int count, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var path = $"/api/v1/namespaces/{Uri.EscapeDataString(item.Namespace)}/events";
        var name = EventName(item);
        if (count > 1)
        {
            var bump = new JsonObject { ["count"] = count, ["lastTimestamp"] = GatewayJson.FormatTime(now) };
            var patched = await _api.SendAsync(HttpMethod.Patch, $"{path}/{Uri.EscapeDataString(name)}", bump.ToJsonString(), "application/merge-patch+json", cancellationToken).ConfigureAwait(false);
            if (patched.IsSuccess)
            {
                return;
            }

            if (!patched.IsNotFound)
            {
                throw new KubeApiException(patched.Status, patched.Body);
            }

            // The API server already dropped the old event: post a fresh one.
            count = 1;
        }

        var involved = new JsonObject
        {
            ["apiVersion"] = $"{IngressApiNames.Group}/{IngressApiNames.Version}",
            ["kind"] = IngressApiNames.Kind,
            ["name"] = item.Name,
            ["namespace"] = item.Namespace,
        };
        if (item.Uid is not null)
        {
            involved["uid"] = item.Uid;
        }

        var body = new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Event",
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = item.Namespace },
            ["involvedObject"] = involved,
            ["reason"] = item.Reason,
            ["message"] = item.Message.Length <= MaxMessage ? item.Message : item.Message[..MaxMessage],
            ["type"] = item.Type,
            ["source"] = new JsonObject { ["component"] = _component },
            ["reportingComponent"] = _component,
            ["firstTimestamp"] = GatewayJson.FormatTime(now),
            ["lastTimestamp"] = GatewayJson.FormatTime(now),
            ["count"] = count,
        };
        var created = await _api.SendAsync(HttpMethod.Post, path, body.ToJsonString(), "application/json", cancellationToken).ConfigureAwait(false);
        if (created.IsSuccess || created.IsConflict)
        {
            return;
        }

        throw new KubeApiException(created.Status, created.Body);
    }

    /// <summary>A stable name per Ingress, reason, and message, so a repeat finds the same Event.</summary>
    internal static string EventName(IngressEvent item)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(item.Key)))[..16];
        var ingress = item.Name.Length <= 200 ? item.Name : item.Name[..200];
        return ingress + "." + hash;
    }
}
