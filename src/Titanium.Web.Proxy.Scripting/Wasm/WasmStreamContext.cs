using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// The proxy-wasm stream (network filter) context of one passthrough connection, pinned to one VM for the life of the
/// connection. Each data piece is loaded as the downstream or upstream buffer; a Pause holds it (it is prepended to the
/// next piece in that direction, as Envoy buffers on StopIteration), unless the guest has a callout in flight, in which
/// case the piece waits until the guest continues the stream.
/// </summary>
internal sealed class WasmStreamContext(WasmVm vm, int id, int rootId, WasmPlugin plugin, StreamConnectionContext connection)
    : WasmContext(vm, id, rootId, plugin)
{
    private readonly byte[]?[] _held = new byte[]?[2];
    private ProxyBufferType? _active;
    private byte[] _data = [];
    private bool _changed;
    private TaskCompletionSource? _resume;

    public StreamConnectionContext Connection { get; } = connection;

    public DateTimeOffset StartTime { get; } = DateTimeOffset.UtcNow;

    /// <summary>The guest closed the connection (<c>proxy_close_stream</c>); every later answer is Deny.</summary>
    public bool Closed { get; private set; }

    /// <summary>True between a callback that answered Pause with work in flight and the guest continuing the stream.</summary>
    public bool Paused { get; private set; }

    public long DownstreamBytes { get; private set; }

    public long UpstreamBytes { get; private set; }

    public int DataLength => _data.Length;

    /// <summary>Loads a piece, after any piece held in the same direction. Returns false past the buffered-body limit.</summary>
    public bool BeginData(ProxyBufferType direction, ReadOnlyMemory<byte> chunk)
    {
        _active = direction;
        _changed = false;
        var slot = Slot(direction);
        if (direction == ProxyBufferType.DownstreamData)
        {
            DownstreamBytes += chunk.Length;
        }
        else
        {
            UpstreamBytes += chunk.Length;
        }

        if (_held[slot] is { } held)
        {
            if (held.Length + (long)chunk.Length > Plugin.Limits.BufferedBodyBytes)
            {
                return false;
            }

            _data = new byte[held.Length + chunk.Length];
            held.CopyTo(_data, 0);
            chunk.Span.CopyTo(_data.AsSpan(held.Length));
            _held[slot] = null;
            _changed = true;
        }
        else
        {
            _data = chunk.ToArray();
        }

        return true;
    }

    /// <summary>
    /// Turns a connection or data callback's answer into a result. Returns null when the stream must wait for the guest
    /// (Pause with a callout in flight, or Pause on a new connection); the caller then waits and calls <see cref="Resolve"/>.
    /// </summary>
    public StreamFilterResult? Finish(int action, bool isData)
    {
        if (Closed)
        {
            return StreamFilterResult.Deny;
        }

        if (action == ProxyAction.Pause)
        {
            if (isData && PendingWork == 0 && _active is { } direction)
            {
                _held[Slot(direction)] = _data;
                _data = [];
                return new StreamFilterResult { ReplaceData = [] };
            }

            Paused = true;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return null;
        }

        return Continue(isData);
    }

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

    /// <summary>The answer once a paused stream resumes, closes, or times out. Called with the VM gate held.</summary>
    public StreamFilterResult Resolve(bool resumed, bool isData)
    {
        if (Closed || !resumed || Paused)
        {
            Paused = false;
            return StreamFilterResult.Deny;
        }

        return Continue(isData);
    }

    /// <summary>The upstream cluster the guest chose with the <see cref="WasmHttpContext.UpstreamClusterProperty"/> property.</summary>
    public string? ChosenCluster =>
        Properties.TryGetValue(WasmHttpContext.UpstreamClusterProperty, out var value) && value.Length > 0
            ? System.Text.Encoding.UTF8.GetString(value)
            : null;

    public override byte[]? GetBuffer(ProxyBufferType type) =>
        type is ProxyBufferType.DownstreamData or ProxyBufferType.UpstreamData && _active == type ? _data : base.GetBuffer(type);

    public override int SetBuffer(ProxyBufferType type, int start, int size, byte[] data)
    {
        if (type is not (ProxyBufferType.DownstreamData or ProxyBufferType.UpstreamData) || _active != type)
        {
            return WasmResult.NotFound;
        }

        _data = Splice(_data, start, size, data);
        _changed = true;
        return WasmResult.Ok;
    }

    public override int ContinueStream(ProxyStreamType type)
    {
        if (type is not (ProxyStreamType.Downstream or ProxyStreamType.Upstream))
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
        if (type is not (ProxyStreamType.Downstream or ProxyStreamType.Upstream))
        {
            return WasmResult.BadArgument;
        }

        Closed = true;
        _resume?.TrySetResult();
        return WasmResult.Ok;
    }

    public override byte[]? GetContextProperty(string path) => WasmProperties.ForStream(this, path);

    public override void OnVmDisposed() => _resume?.TrySetResult();

    /// <summary>Ends a data callback: the active piece is no longer the guest's buffer.</summary>
    public void EndData() => _active = null;

    private StreamFilterResult Continue(bool isData)
    {
        if (isData)
        {
            return _changed ? new StreamFilterResult { ReplaceData = _data } : StreamFilterResult.Continue;
        }

        return ChosenCluster is { } cluster ? new StreamFilterResult { ClusterId = cluster } : StreamFilterResult.Continue;
    }

    private static int Slot(ProxyBufferType direction) => direction == ProxyBufferType.DownstreamData ? 0 : 1;
}
