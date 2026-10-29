using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// The proxy-wasm HTTP context of one request. It is pinned to one VM for the life of the request. Each phase begins by
/// loading the message the phase concerns, the guest edits it through host functions, and <see cref="Finish"/> turns the
/// result into a <see cref="ScriptPhaseResult"/>.
/// </summary>
internal sealed class WasmHttpContext(WasmVm vm, int id, int rootId, WasmPlugin plugin) : WasmContext(vm, id, rootId, plugin)
{
    /// <summary>Path set with <c>proxy_set_property</c> that sends the request to another cluster (Titanium extension).</summary>
    public const string UpstreamClusterProperty = "twp.upstream_cluster";

    private WasmHeaderMap? _activeMap;
    private byte[] _body = [];
    private bool _bodyChanged;
    private bool _bodyEnd;
    private byte[]? _held;
    private TaskCompletionSource? _resume;

    public long CreatedTicks { get; } = Environment.TickCount64;

    public DateTimeOffset StartTime { get; } = DateTimeOffset.UtcNow;

    /// <summary>The newest phase context Core passed; properties read request and response facts from it.</summary>
    public ScriptPhaseContext? Last { get; set; }

    public WasmHeaderMap? RequestHeaders { get; private set; }

    public WasmHeaderMap? RequestTrailers { get; private set; }

    public WasmHeaderMap? ResponseHeaders { get; private set; }

    public WasmHeaderMap? ResponseTrailers { get; private set; }

    public ScriptPhases ActivePhase { get; private set; }

    public ScriptLocalResponse? LocalResponse { get; private set; }

    /// <summary>True between a callback that answered Pause and the guest resuming (or answering) it.</summary>
    public bool Paused { get; private set; }

    /// <summary>Work the guest started that can still resume a paused request (callouts in flight).</summary>
    public int PendingWork { get; set; }

    public long RequestBodyBytes { get; private set; }

    public long ResponseBodyBytes { get; private set; }

    public void BeginHeaders(ScriptPhases phase, WasmHeaderMap map)
    {
        ActivePhase = phase;
        _activeMap = map;
        switch (phase)
        {
            case ScriptPhases.RequestHeaders:
                RequestHeaders = map;
                break;
            case ScriptPhases.RequestTrailers:
                RequestTrailers = map;
                break;
            case ScriptPhases.ResponseHeaders:
                ResponseHeaders = map;
                break;
            case ScriptPhases.ResponseTrailers:
                ResponseTrailers = map;
                break;
        }
    }

    /// <summary>
    /// Loads a body piece. A piece the guest asked to hold (Pause before the end of the body) is prepended, so the guest
    /// sees everything since its last Continue, as with Envoy. Returns false when that exceeds the buffered-body limit.
    /// </summary>
    public bool BeginBody(ScriptPhases phase, ReadOnlyMemory<byte> chunk, bool end)
    {
        ActivePhase = phase;
        _activeMap = null;
        _bodyEnd = end;
        _bodyChanged = false;
        if (phase == ScriptPhases.RequestBody)
        {
            RequestBodyBytes += chunk.Length;
        }
        else
        {
            ResponseBodyBytes += chunk.Length;
        }

        if (_held is { } held)
        {
            if (held.Length + (long)chunk.Length > Plugin.Limits.BufferedBodyBytes)
            {
                return false;
            }

            _body = new byte[held.Length + chunk.Length];
            held.CopyTo(_body, 0);
            chunk.Span.CopyTo(_body.AsSpan(held.Length));
            _held = null;
            _bodyChanged = true;
        }
        else
        {
            _body = chunk.ToArray();
        }

        return true;
    }

    public int BodyLength => _body.Length;

    public bool BodyEnd => _bodyEnd;

    /// <summary>Turns the callback's answer into the phase result. Called with the VM gate held.</summary>
    public ScriptPhaseResult Finish(int action)
    {
        if (LocalResponse is { } local)
        {
            return new ScriptPhaseResult { Action = ScriptAction.Deny, LocalResponse = local };
        }

        if (action == ProxyAction.Pause)
        {
            var isBody = ActivePhase is ScriptPhases.RequestBody or ScriptPhases.ResponseBody;
            if (isBody && !_bodyEnd && PendingWork == 0)
            {
                // Hold this piece and hand the guest more next time; nothing is forwarded yet.
                _held = _body;
                _body = [];
                return new ScriptPhaseResult { ReplaceBody = [] };
            }

            Paused = true;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return ScriptPhaseResult.Pause;
        }

        return BuildContinue();
    }

    /// <summary>Waits until the guest resumes or answers a paused request, the VM goes away, or the wait times out.</summary>
    public async ValueTask<bool> WaitForResumeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var resume = _resume;
        if (!Paused || resume is null)
        {
            return true;
        }

        try
        {
            await resume.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>The answer once a paused request is resumed. Called with the VM gate held.</summary>
    public ScriptPhaseResult Resolve(bool resumed)
    {
        if (LocalResponse is { } local)
        {
            Paused = false;
            return new ScriptPhaseResult { Action = ScriptAction.Deny, LocalResponse = local };
        }

        if (!resumed || Paused)
        {
            Paused = false;
            return ScriptPhaseResult.Deny(504, "WASM plugin did not resume the request.");
        }

        return BuildContinue();
    }

    public override byte[]? GetBuffer(ProxyBufferType type) => type switch
    {
        ProxyBufferType.HttpRequestBody when ActivePhase == ScriptPhases.RequestBody => _body,
        ProxyBufferType.HttpResponseBody when ActivePhase == ScriptPhases.ResponseBody => _body,
        _ => base.GetBuffer(type),
    };

    public override int SetBuffer(ProxyBufferType type, int start, int size, byte[] data)
    {
        var active = type switch
        {
            ProxyBufferType.HttpRequestBody => ActivePhase == ScriptPhases.RequestBody,
            ProxyBufferType.HttpResponseBody => ActivePhase == ScriptPhases.ResponseBody,
            _ => false,
        };
        if (!active)
        {
            return WasmResult.NotFound;
        }

        _body = Splice(_body, start, size, data);
        _bodyChanged = true;
        return WasmResult.Ok;
    }

    public override WasmHeaderMap? GetMap(ProxyMapType type) => type switch
    {
        ProxyMapType.HttpRequestHeaders => RequestHeaders,
        ProxyMapType.HttpRequestTrailers => RequestTrailers,
        ProxyMapType.HttpResponseHeaders => ResponseHeaders,
        ProxyMapType.HttpResponseTrailers => ResponseTrailers,
        _ => Vm.GetCalloutMap(type),
    };

    public override int SendLocalResponse(int status, string details, byte[] body, List<KeyValuePair<string, string>> headers, int grpcStatus)
    {
        if (grpcStatus >= 0)
        {
            headers.Add(new("grpc-status", grpcStatus.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        LocalResponse = new ScriptLocalResponse
        {
            Status = status is >= 100 and <= 999 ? status : 500,
            Headers = headers,
            Body = body,
        };
        _resume?.TrySetResult();
        return WasmResult.Ok;
    }

    public override int ContinueStream(ProxyStreamType type)
    {
        if (type is not (ProxyStreamType.HttpRequest or ProxyStreamType.HttpResponse))
        {
            return WasmResult.BadArgument;
        }

        if (Paused)
        {
            Paused = false;
            _resume?.TrySetResult();
        }

        return WasmResult.Ok;
    }

    public override int CloseStream(ProxyStreamType type)
    {
        if (type is not (ProxyStreamType.HttpRequest or ProxyStreamType.HttpResponse))
        {
            return WasmResult.BadArgument;
        }

        LocalResponse ??= new ScriptLocalResponse { Status = 503 };
        _resume?.TrySetResult();
        return WasmResult.Ok;
    }

    public override byte[]? GetContextProperty(string path) => WasmProperties.ForHttp(this, path);

    public override void OnVmDisposed() => _resume?.TrySetResult();

    private ScriptPhaseResult BuildContinue()
    {
        var phase = ActivePhase;
        Dictionary<string, string>? set = null;
        List<string>? remove = null;
        if (_activeMap is { } map)
        {
            (set, remove) = map.Diff();
        }

        byte[]? body = null;
        if (phase is ScriptPhases.RequestBody or ScriptPhases.ResponseBody && _bodyChanged)
        {
            body = _body;
        }

        string? cluster = null;
        if (phase == ScriptPhases.RequestHeaders && Properties.TryGetValue(UpstreamClusterProperty, out var value) && value.Length > 0)
        {
            cluster = System.Text.Encoding.UTF8.GetString(value);
        }

        if (set is null && remove is null && body is null && cluster is null)
        {
            return ScriptPhaseResult.Continue;
        }

        return new ScriptPhaseResult
        {
            SetHeaders = set,
            RemoveHeaders = remove,
            ReplaceBody = body,
            UpstreamClusterId = cluster,
        };
    }
}
