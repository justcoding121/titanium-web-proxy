using System.Net;

namespace Titanium.Web.Proxy.Abstractions.Scripting;

/// <summary>What a script phase callback asks the host to do next.</summary>
public enum ScriptAction
{
    /// <summary>Apply any edits in the result and carry on.</summary>
    Continue = 0,

    /// <summary>
    /// The script started asynchronous work. The host calls <see cref="IScriptPhaseHost.OnResumeAsync"/> until the
    /// script answers with <see cref="Continue"/> or <see cref="Deny"/>. Only HTTP phases pause; an L4 filter simply awaits.
    /// </summary>
    Pause = 1,

    /// <summary>Stop the message. The host answers with <see cref="ScriptPhaseResult.LocalResponse"/> or a 403.</summary>
    Deny = 2,
}

/// <summary>The HTTP phases a script host can observe. A host lists the ones it wants in <see cref="IScriptPhaseHost.Phases"/>.</summary>
[Flags]
public enum ScriptPhases
{
    None = 0,
    RequestHeaders = 1,
    RequestBody = 2,
    ResponseHeaders = 4,
    ResponseBody = 8,
    RequestTrailers = 16,
    ResponseTrailers = 32,
    Log = 64,
    All = RequestHeaders | RequestBody | ResponseHeaders | ResponseBody | RequestTrailers | ResponseTrailers | Log,
}

/// <summary>How a script host wants to see message bodies.</summary>
public enum ScriptBodyMode
{
    /// <summary>The callback runs once per piece as the body is relayed. Memory stays flat.</summary>
    Streaming = 0,

    /// <summary>The host buffers the whole body (bounded by the buffered-body limit) and calls back once with the last-chunk flag set.</summary>
    Buffered = 1,
}

/// <summary>A response a script supplies instead of forwarding the request (or instead of the origin's answer).</summary>
public sealed class ScriptLocalResponse
{
    public int Status { get; init; } = 403;
    public string? ContentType { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; init; }
    public byte[]? Body { get; init; }
}

/// <summary>What a script phase callback answers. <see cref="Continue"/> is a shared instance, so the common answer allocates nothing.</summary>
public sealed class ScriptPhaseResult
{
    /// <summary>Carry on with no edits.</summary>
    public static readonly ScriptPhaseResult Continue = new();

    /// <summary>Ask the host to call <see cref="IScriptPhaseHost.OnResumeAsync"/>.</summary>
    public static readonly ScriptPhaseResult Pause = new() { Action = ScriptAction.Pause };

    public ScriptAction Action { get; init; }

    /// <summary>Headers to set (replace or add) on the message the phase sees. For trailers they are added to the trailer block.</summary>
    public IReadOnlyDictionary<string, string>? SetHeaders { get; init; }

    /// <summary>Header names to remove from the message the phase sees.</summary>
    public IReadOnlyList<string>? RemoveHeaders { get; init; }

    /// <summary>Replaces the body piece (streaming) or the whole body (buffered). Null keeps it.</summary>
    public byte[]? ReplaceBody { get; init; }

    /// <summary>
    /// Request-headers phase only: send this request to the named cluster. The host re-resolves the destination
    /// through the normal load-balancer path, so affinity, health, and limits of that cluster apply.
    /// </summary>
    public string? UpstreamClusterId { get; init; }

    /// <summary>Used with <see cref="ScriptAction.Deny"/>. Null means a plain 403.</summary>
    public ScriptLocalResponse? LocalResponse { get; init; }

    /// <summary>A deny with a plain status and optional text body.</summary>
    public static ScriptPhaseResult Deny(int status = 403, string? body = null) => new()
    {
        Action = ScriptAction.Deny,
        LocalResponse = new ScriptLocalResponse
        {
            Status = status,
            ContentType = body is null ? null : "text/plain",
            Body = body is null ? null : System.Text.Encoding.UTF8.GetBytes(body),
        },
    };
}

/// <summary>What the host tells a script about the message at a phase. Built only when a script host is configured.</summary>
public sealed class ScriptPhaseContext
{
    public ScriptPhases Phase { get; init; }
    public string? RouteId { get; init; }
    public string? ClusterId { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public bool IsHttps { get; init; }
    public IPEndPoint? ClientEndPoint { get; init; }

    /// <summary>Response status. Zero before the origin has answered.</summary>
    public int StatusCode { get; init; }

    /// <summary>The headers (or trailers) of the message this phase concerns, as a snapshot.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>The body piece (streaming) or whole body (buffered). Empty outside body phases.</summary>
    public ReadOnlyMemory<byte> Chunk { get; init; }

    /// <summary>True on the final body piece, and always for a buffered body.</summary>
    public bool IsLastChunk { get; init; }

    /// <summary>How many times <see cref="IScriptPhaseHost.OnResumeAsync"/> has run for this phase. Zero on the first call.</summary>
    public int ResumeCount { get; set; }

    /// <summary>Request duration for the log phase.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Failure text for the log phase, null on success.</summary>
    public string? Error { get; init; }

    /// <summary>State a script keeps across the phases of one request. Never shared between requests.</summary>
    public required IDictionary<string, object?> Items { get; init; }
}
