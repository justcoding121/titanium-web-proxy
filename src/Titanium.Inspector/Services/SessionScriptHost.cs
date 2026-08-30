using System.Net;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Inspector.Services;

/// <summary>
/// Lightweight sandboxed hooks: one directive per line —
/// <c>set-header Name: Value</c>, <c>set-status 404</c>, or <c>abort</c>.
/// </summary>
public static class SessionScriptHost
{
    public sealed class Result
    {
        public bool Abort { get; set; }
        public int? StatusCode { get; set; }
        public List<(string Name, string Value)> Headers { get; } = new();
    }

    public static Result Interpret(string? script)
    {
        var result = new Result();
        if (string.IsNullOrWhiteSpace(script))
        {
            return result;
        }

        foreach (var raw in script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            ApplyDirective(result, raw.Trim());
        }

        return result;
    }

    private static void ApplyDirective(Result result, string line)
    {
        if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
        {
            return;
        }

        if (line.Equals("abort", StringComparison.OrdinalIgnoreCase))
        {
            result.Abort = true;
            return;
        }

        if (line.StartsWith("set-status", StringComparison.OrdinalIgnoreCase))
        {
            var rest = line["set-status".Length..].Trim();
            if (int.TryParse(rest, out var code))
            {
                result.StatusCode = code;
            }

            return;
        }

        if (line.StartsWith("set-header", StringComparison.OrdinalIgnoreCase))
        {
            var rest = line["set-header".Length..].Trim();
            var colon = rest.IndexOf(':');
            if (colon > 0)
            {
                result.Headers.Add((rest[..colon].Trim(), rest[(colon + 1)..].Trim()));
            }
        }
    }

    public static bool ApplyOnRequest(string? script, SessionEventArgs e)
    {
        var result = Interpret(script);
        foreach (var (name, value) in result.Headers)
        {
            e.HttpClient.Request.Headers.RemoveHeader(name);
            e.HttpClient.Request.Headers.AddHeader(name, value);
        }

        if (result.Abort)
        {
            var status = (HttpStatusCode)(result.StatusCode ?? 403);
            e.GenericResponse("Aborted by Titanium Inspector request script", status);
            return true;
        }

        if (result.StatusCode is int code)
        {
            e.GenericResponse(string.Empty, (HttpStatusCode)code);
            return true;
        }

        return false;
    }

    public static bool ApplyOnResponse(string? script, SessionEventArgs e)
    {
        var result = Interpret(script);
        foreach (var (name, value) in result.Headers)
        {
            e.HttpClient.Response.Headers.RemoveHeader(name);
            e.HttpClient.Response.Headers.AddHeader(name, value);
        }

        if (result.StatusCode is int code)
        {
            e.HttpClient.Response.StatusCode = code;
        }

        if (result.Abort)
        {
            var status = (HttpStatusCode)(result.StatusCode ?? 403);
            e.GenericResponse("Aborted by Titanium Inspector response script", status);
            return true;
        }

        return false;
    }
}
