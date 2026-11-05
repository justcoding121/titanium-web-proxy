using System.Net;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>Builds the encoded <c>ctx</c> a phase function receives. Lua reads it; nothing in it points back to the host.</summary>
internal static class LuaSnapshot
{
    public static string PhaseName(ScriptPhases phase) => phase switch
    {
        ScriptPhases.RequestHeaders => "request_headers",
        ScriptPhases.RequestBody => "request_body",
        ScriptPhases.RequestTrailers => "request_trailers",
        ScriptPhases.ResponseHeaders => "response_headers",
        ScriptPhases.ResponseBody => "response_body",
        ScriptPhases.ResponseTrailers => "response_trailers",
        ScriptPhases.Log => "log",
        _ => "unknown",
    };

    public static string FunctionName(ScriptPhases phase) => "on_" + PhaseName(phase);

    public static byte[] Http(string plugin, ScriptPhaseContext context, byte[]? vars)
    {
        var w = new LuaWriter(512 + context.Chunk.Length);
        var mark = w.BeginTable();
        var pairs = 0;
        w.Field("phase", PhaseName(context.Phase));
        w.Field("plugin", plugin);
        w.Field("method", context.Method);
        w.Field("path", context.Path);
        w.Field("host", context.Host);
        w.Field("scheme", context.IsHttps ? "https" : "http");
        w.Field("is_https", context.IsHttps);
        pairs += 7;
        if (context.RouteId is not null)
        {
            w.Field("route_id", context.RouteId);
            pairs++;
        }

        if (context.ClusterId is not null)
        {
            w.Field("cluster_id", context.ClusterId);
            pairs++;
        }

        pairs += EndPoint(w, "client", context.ClientEndPoint);
        if (context.StatusCode != 0)
        {
            w.Field("status", context.StatusCode);
            pairs++;
        }

        w.String("headers");
        Headers(w, context.Headers);
        pairs++;

        if (context.Phase is ScriptPhases.RequestBody or ScriptPhases.ResponseBody)
        {
            w.Field("body", context.Chunk.Span);
            w.Field("last", context.IsLastChunk);
            pairs += 2;
        }

        if (context.Phase == ScriptPhases.Log)
        {
            w.Field("elapsed_ms", (long)context.Elapsed.TotalMilliseconds);
            pairs++;
            if (context.Error is not null)
            {
                w.Field("error", context.Error);
                pairs++;
            }
        }

        if (vars is not null)
        {
            w.String("vars");
            w.Raw(vars);
            pairs++;
        }

        w.EndTable(mark, pairs);
        return w.ToArray();
    }

    public static void Headers(LuaWriter w, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var mark = w.BeginTable();
        for (var i = 0; i < headers.Count; i++)
        {
            w.Int(i + 1);
            var entry = w.BeginTable();
            w.Int(1);
            w.String(headers[i].Key);
            w.Int(2);
            w.String(headers[i].Value);
            w.EndTable(entry, 2);
        }

        w.EndTable(mark, headers.Count);
    }

    public static int EndPoint(LuaWriter w, string prefix, EndPoint? endPoint)
    {
        if (endPoint is not IPEndPoint ip)
        {
            return 0;
        }

        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        w.Field(prefix + "_ip", address.ToString());
        w.Field(prefix + "_port", ip.Port);
        return 2;
    }
}
