namespace Titanium.Web.Proxy.Abstractions.Scripting;

/// <summary>
/// Optional listener-wide hook set for an embedded scripting engine (Lua or WASM). Core calls it only when
/// <see cref="ReverseProxyOptions.ScriptPipeline"/> is set; with none set the request path pays one null check per seam.
/// Every callback has a default that continues, so a host overrides only what it needs and lists those phases in
/// <see cref="Phases"/>.
/// </summary>
public interface IScriptPhaseHost
{
    /// <summary>The phases Core should call. A phase left out costs nothing, not even a context.</summary>
    ScriptPhases Phases => ScriptPhases.All;

    /// <summary>Whether body callbacks see pieces as they stream or one buffered body.</summary>
    ScriptBodyMode BodyMode => ScriptBodyMode.Streaming;

    ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    /// <summary>Runs once per request after the response was sent. The answer is ignored; a throw is swallowed.</summary>
    ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken) => default;

    /// <summary>
    /// Called after a callback answered <see cref="ScriptAction.Pause"/>, with the same context (and
    /// <see cref="ScriptPhaseContext.ResumeCount"/> advanced). Core stops after a bounded number of resumes and denies with 504.
    /// </summary>
    ValueTask<ScriptPhaseResult> OnResumeAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);
}
