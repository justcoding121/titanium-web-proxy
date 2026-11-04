using System.Net;
using System.Text;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Tests.Lua;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Fuzz;

/// <summary>An input broke a host-call invariant, or made the host throw where it must answer with an error code.</summary>
internal sealed class WasmFuzzInvariantException(string message) : Exception(message);

/// <summary>
/// Deterministic fuzz target for the two places a proxy-wasm guest hands the host bytes it controls: the serialized
/// header map (<c>proxy_set_header_map_pairs</c>, <c>proxy_send_local_response</c>, <c>proxy_http_call</c>) and the property
/// path (<c>proxy_get_property</c>, <c>proxy_set_property</c>). Neither needs the Wasmtime native: the guest memory is just a
/// byte span at that point. A malformed map must come back as <c>null</c> (the host answers <c>ParseFailure</c>), a
/// malformed path as <c>null</c> (<c>NotFound</c>); neither may throw, hang, or hand back a path with a NUL in it.
/// <para>
///     <see cref="HeaderMap(byte[])" /> and <see cref="PropertyPath(byte[])" /> are the entry points an external fuzzer
///     (for example SharpFuzz) wraps. See <c>tests/fuzz/README.md</c>.
/// </para>
/// </summary>
internal static class WasmHostCallFuzzTarget
{
    /// <summary>What one header-map decode produced.</summary>
    internal sealed record HeaderMapOutcome(bool Parsed, int Count, IReadOnlyList<string> Violations);

    /// <summary>SharpFuzz-style entry point. Throws <see cref="WasmFuzzInvariantException" /> on a violation.</summary>
    public static void HeaderMap(byte[] data)
    {
        var outcome = CheckHeaderMap(data);
        if (outcome.Violations.Count > 0)
        {
            throw new WasmFuzzInvariantException(string.Join("; ", outcome.Violations));
        }
    }

    /// <summary>SharpFuzz-style entry point. Throws <see cref="WasmFuzzInvariantException" /> on a violation.</summary>
    public static void PropertyPath(byte[] data)
    {
        var violations = CheckPropertyPath(data, out _);
        if (violations.Count > 0)
        {
            throw new WasmFuzzInvariantException(string.Join("; ", violations));
        }
    }

    /// <summary>Decodes <paramref name="data"/> the way a host function does and checks what comes out.</summary>
    internal static HeaderMapOutcome CheckHeaderMap(byte[] data)
    {
        var violations = new List<string>();
        List<KeyValuePair<string, string>>? pairs = null;
        try
        {
            pairs = WasmHeaderMap.Deserialize(data);

            var again = WasmHeaderMap.Deserialize(data);
            if (!SameSequence(pairs, again))
            {
                violations.Add("a second decode of the same bytes gave a different answer");
            }

            if (pairs is not null)
            {
                CheckParsed(data, pairs, violations);
            }
        }
        catch (Exception ex)
        {
            violations.Add($"unexpected {ex.GetType().Name}: {ex.Message}");
        }

        return new HeaderMapOutcome(pairs is not null, pairs?.Count ?? 0, violations);
    }

    private static void CheckParsed(byte[] data, List<KeyValuePair<string, string>> pairs, List<string> violations)
    {
        if (data.Length > 0 && pairs.Count > (data.Length - 4) / 8)
        {
            violations.Add($"{pairs.Count} pairs decoded from only {data.Length} bytes");
        }

        // What the host writes back must decode to exactly what it holds.
        var wire = WasmHeaderMap.Serialize(pairs);
        var back = WasmHeaderMap.Deserialize(wire);
        if (!SameSequence(pairs, back))
        {
            violations.Add("Serialize then Deserialize did not give the pairs back");
        }

        var map = new WasmHeaderMap(pairs);
        var (set, remove) = map.Diff();
        if (set is not null || remove is not null)
        {
            violations.Add("a map the guest did not touch reports edits");
        }

        // The guest edits that map, then the host turns the edits into set and remove lists.
        if (pairs.Count > 0)
        {
            var first = pairs[0].Key;
            map.Replace(first, "x");
            map.Add(first, "y");
            map.Remove(first);
            map.Add("fuzz-added", "1");
        }

        map.SetAll(pairs);
        _ = map.Diff();
        _ = map.Get(pairs.Count > 0 ? pairs[0].Key : "x");
        if (map.Serialize().Length < 4)
        {
            violations.Add("a serialized map is shorter than its count field");
        }
    }

    /// <summary>
    /// Reads <paramref name="data"/> as a guest-supplied property path and runs it through the normalizer and the HTTP and
    /// stream property tables. <paramref name="httpValue"/> is the HTTP answer as text, or null for NotFound.
    /// </summary>
    internal static List<string> CheckPropertyPath(byte[] data, out string? httpValue)
    {
        httpValue = null;
        var violations = new List<string>();
        try
        {
            var raw = Encoding.UTF8.GetString(data);
            var path = WasmProperties.NormalizePath(raw);

            if (path.Contains('\0'))
            {
                violations.Add("a normalized path still contains a NUL");
            }

            if (path.EndsWith('.'))
            {
                violations.Add("a normalized path still ends in a dot");
            }

            if (WasmProperties.NormalizePath(path) != path)
            {
                violations.Add("normalizing a normalized path changed it");
            }

            var http = WasmProperties.ForHttp(HttpContext(), path);
            httpValue = http is null ? null : Encoding.UTF8.GetString(http);
            _ = WasmProperties.ForHttp(EmptyHttpContext(), path);
            _ = WasmProperties.ForStream(StreamContext(), path);
        }
        catch (Exception ex)
        {
            violations.Add($"unexpected {ex.GetType().Name}: {ex.Message}");
        }

        return violations;
    }

    private static WasmHttpContext HttpContext()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!)
        {
            Last = LuaTestSupport.Phase(ScriptPhases.RequestHeaders),
        };
        context.BeginHeaders(ScriptPhases.RequestHeaders, new WasmHeaderMap(
        [
            new(":path", "/foo?x=1"),
            new(":method", "GET"),
            new(":authority", "example.test"),
            new("user-agent", "fuzz"),
            new("content-length", "3"),
        ]));
        return context;
    }

    private static WasmHttpContext EmptyHttpContext() => new(null!, 2, 2, null!);

    private static WasmStreamContext StreamContext() => new(null!, 3, 3, null!, new StreamConnectionContext
    {
        ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 5555),
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
        ServerName = "sni.test",
        ClusterId = "a",
        Items = new Dictionary<string, object?>(),
    });

    private static bool SameSequence(List<KeyValuePair<string, string>>? a, List<KeyValuePair<string, string>>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Key, b[i].Key, StringComparison.Ordinal) ||
                !string.Equals(a[i].Value, b[i].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
