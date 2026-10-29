using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Scripting.Lua.Native;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// One loaded Lua script: a pool of states plus the host side of every call. A call starts on a pooled state, and each time
/// the script waits (a host operation such as a callout) the state's gate is released, the operation runs here, and the
/// call resumes on the same state with the result. Instruction budget and CPU deadline span all segments of a call; the
/// wall timeout (<see cref="LuaOptions.Timeout"/>) is the backstop.
/// </summary>
internal sealed partial class LuaRuntime : IDisposable
{
    public const string OnConfigure = "on_configure";

    public static readonly string[] EntryPoints =
    [
        OnConfigure, "on_request_headers", "on_request_body", "on_request_trailers", "on_response_headers", "on_response_body",
        "on_response_trailers", "on_log", "on_connect", "on_data", "on_close",
    ];

    private static readonly byte[] NoValues = [(byte)'T', 0, 0, 0, 0];

    private readonly byte[] _settings;
    private readonly CancellationTokenSource _disposed = new();
    private readonly LuaCallouts _callouts;
    private readonly LuaJwt _jwt;
    private LuaPool? _pool;
    private int _configureTimers;

    private LuaRuntime(LuaOptions options, ILogger? logger, HttpMessageHandler? calloutHandler)
    {
        Options = options;
        Logger = logger;
        _callouts = new LuaCallouts(options, calloutHandler);
        _jwt = LuaJwt.FromSettings(options.Settings);
        var writer = new LuaWriter();
        var mark = writer.BeginTable();
        foreach (var pair in options.Settings)
        {
            writer.Field(pair.Key, pair.Value);
        }

        writer.EndTable(mark, options.Settings.Count);
        _settings = writer.ToArray();
    }

    public LuaOptions Options { get; }

    public ILogger? Logger { get; }

    public string Name => Options.Name;

    public LuaPool Pool => _pool ?? throw new ObjectDisposedException(nameof(LuaRuntime));

    /// <summary>Entry points the script defines (the same in every state).</summary>
    public IReadOnlySet<string> Functions { get; private set; } = new HashSet<string>();

    internal CancellationToken Stopping => _disposed.Token;

    /// <summary>Callouts waiting on an answer right now.</summary>
    public int CalloutsInFlight => _callouts.InFlight;

    /// <summary>
    /// Compiles the script into every pooled state and runs <c>on_configure</c> in each before returning.
    /// <paramref name="calloutHandler"/> replaces the network for <c>http.call</c> (tests).
    /// </summary>
    public static LuaRuntime Create(LuaOptions options, ILogger? logger, HttpMessageHandler? calloutHandler = null)
    {
        var runtime = new LuaRuntime(options, logger, calloutHandler);
        try
        {
            foreach (var (name, maxItems) in options.SharedDicts)
            {
                LuaCalls.DictConfigure(name, maxItems, options.SharedDictBytes);
            }

            runtime._pool = LuaPool.Create(options.Name, options.PoolSize, runtime.CreateInstance, logger);
            runtime.Functions = runtime._pool.Live.First().Functions;
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    public bool Defines(string function) => Functions.Contains(function);

    private LuaInstance CreateInstance()
    {
        var handle = LuaStateHandle.Create(Options.MemoryBytes, Options.OutputBytes);
        try
        {
            if (!LuaCalls.SetSettings(handle, _settings))
            {
                throw new LuaScriptException(LuaFailure.Load, "settings could not be passed to the script");
            }

            LuaCalls.Load(handle, Options.Source, "@" + Path.GetFileName(Options.File), Options.MaxInstructions, Micros(Options.EngineDeadline));
            var functions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in EntryPoints)
            {
                if (LuaCalls.HasFunction(handle, name))
                {
                    functions.Add(name);
                }
            }

            if (functions.Contains(OnConfigure))
            {
                Configure(handle);
            }

            return new LuaInstance(handle, functions);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private void Configure(LuaStateHandle handle)
    {
        var writer = new LuaWriter();
        var mark = writer.BeginTable();
        writer.Field("phase", "configure");
        writer.Field("plugin", Name);
        writer.EndTable(mark, 2);
        var segment = LuaCalls.Start(handle, OnConfigure, writer.Written, Options.MaxInstructions, Micros(Options.EngineDeadline));
        if (segment.Code == LuaNative.Yield)
        {
            LuaCalls.Cancel(handle, segment.CallId);
            throw new LuaScriptException(LuaFailure.Load, "on_configure cannot wait (no http.call, time.sleep, or jwt.verify)");
        }

        // Every state runs on_configure, but timers it starts belong to the plugin, so only the first state's count.
        Finish(segment, acceptTimers: Interlocked.Exchange(ref _configureTimers, 1) == 0);
    }

    /// <summary>
    /// Runs <paramref name="function"/> with <paramref name="snapshot"/> as <c>ctx</c>. Throws <see cref="LuaScriptException"/>
    /// when the script fails or the wall timeout passes; throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async ValueTask<LuaCallResult> CallAsync(string function, ReadOnlyMemory<byte> snapshot, CancellationToken cancellationToken)
    {
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        wall.CancelAfter(Options.Timeout);
        var token = wall.Token;
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await RunAsync(function, snapshot, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = new LuaScriptException(LuaFailure.Timeout,
                $"Lua plugin '{Name}' did not finish {function} within {Options.Timeout.TotalMilliseconds:0} ms");
            LuaMetrics.RecordError(Name, LuaScriptException.Reason(failure.Failure));
            throw failure;
        }
        catch (LuaScriptException ex)
        {
            LuaMetrics.RecordError(Name, LuaScriptException.Reason(ex.Failure));
            throw;
        }
        finally
        {
            LuaMetrics.RecordCall(Name, function, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async ValueTask<LuaCallResult> RunAsync(string function, ReadOnlyMemory<byte> snapshot, CancellationToken token)
    {
        var instance = await Pool.AcquireAsync(token).ConfigureAwait(false);
        var gateHeld = true;
        var callId = 0;
        var waiting = false;
        var budget = Options.MaxInstructions;
        var cpu = Options.EngineDeadline;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var segment = LuaCalls.Start(instance.Handle, function, snapshot.Span, budget, Micros(cpu));
            cpu -= Stopwatch.GetElapsedTime(started);
            budget -= segment.Used;
            callId = segment.CallId;

            while (segment.Code == LuaNative.Yield)
            {
                waiting = true;
                instance.Gate.Release();
                gateHeld = false;

                var values = await RunOperationAsync(segment.Output, token).ConfigureAwait(false);

                await instance.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                gateHeld = true;
                started = Stopwatch.GetTimestamp();
                segment = LuaCalls.Resume(instance.Handle, callId, values, Math.Max(1, budget), Math.Max(1, Micros(cpu)));
                cpu -= Stopwatch.GetElapsedTime(started);
                budget -= segment.Used;
                waiting = false;
            }

            if (LuaScriptException.IsLimit(LuaScriptException.FailureOf(segment.Code)) && segment.Code != LuaNative.Ok)
            {
                // The state stays usable for calls already suspended on it, but takes no new ones.
                Pool.Discard(instance);
                LuaMetrics.RecordRetired(Name, LuaScriptException.Reason(LuaScriptException.FailureOf(segment.Code)));
            }

            return Finish(segment, acceptTimers: true);
        }
        finally
        {
            if (waiting)
            {
                // Abandoned while waiting (timeout or cancellation): drop the suspended coroutine.
                if (!gateHeld)
                {
                    await instance.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    gateHeld = true;
                }

                LuaCalls.Cancel(instance.Handle, callId);
            }

            if (gateHeld)
            {
                instance.Gate.Release();
            }

            instance.Exit();
        }
    }

    /// <summary>Runs one host operation the script is waiting on and returns the encoded values to resume it with.</summary>
    private async ValueTask<byte[]> RunOperationAsync(byte[] output, CancellationToken token)
    {
        var reader = new LuaReader(output);
        var envelope = reader.Read() as LuaTable;
        var op = envelope?.Table("op");
        var kind = op?.Str("op");
        var result = await RunOperationAsync(kind, op, token).ConfigureAwait(false);
        return result ?? Values(null, $"{kind ?? "this operation"} is not available");
    }

    private partial ValueTask<byte[]?> RunOperationAsync(string? kind, LuaTable? op, CancellationToken token);

    /// <summary>Encodes resume values: an array of up to 16 values, with <c>n</c> so a leading nil survives.</summary>
    internal static byte[] Values(params object?[] values)
    {
        if (values.Length == 0)
        {
            return NoValues;
        }

        var writer = new LuaWriter();
        var mark = writer.BeginTable();
        writer.Field("n", values.Length);
        var pairs = 1;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is null)
            {
                continue;
            }

            writer.Int(i + 1);
            writer.Value(values[i]);
            pairs++;
        }

        writer.EndTable(mark, pairs);
        return writer.ToArray();
    }

    private LuaCallResult Finish(LuaSegment segment, bool acceptTimers)
    {
        var output = segment.Output;
        if (output.Length == 0)
        {
            if (segment.Code == LuaNative.Ok)
            {
                return LuaCallResult.Empty;
            }

            throw new LuaScriptException(LuaScriptException.FailureOf(segment.Code), $"Lua plugin '{Name}' failed (code {segment.Code})");
        }

        var result = new LuaCallResult();
        string? error = null;
        var reader = new LuaReader(output);
        var count = reader.ReadTableHeader();
        for (var i = 0; i < count; i++)
        {
            var key = LuaReader.AsString(reader.Read());
            switch (key)
            {
                case "vars":
                    var raw = reader.ReadRaw();
                    result.Vars = raw.Length > 1 ? raw.ToArray() : null;
                    break;
                case "cmds":
                    Apply(reader.Read() as LuaTable, result, acceptTimers);
                    break;
                case "ret":
                    result.Return = reader.Read();
                    break;
                case "error":
                    error = LuaReader.AsString(reader.Read());
                    break;
                default:
                    reader.Read();
                    break;
            }
        }

        if (segment.Code != LuaNative.Ok)
        {
            var failure = LuaScriptException.FailureOf(segment.Code);
            throw new LuaScriptException(failure, error ?? $"Lua plugin '{Name}' failed ({LuaScriptException.Reason(failure)})");
        }

        return result;
    }

    private void Apply(LuaTable? commands, LuaCallResult result, bool acceptTimers)
    {
        if (commands is null)
        {
            return;
        }

        foreach (var item in commands.Array)
        {
            if (item is not LuaTable command)
            {
                continue;
            }

            switch (LuaReader.AsString(command[1]))
            {
                case "set_header" when LuaReader.AsString(command[2]) is { } name && LuaReader.AsString(command[3]) is { } value:
                    (result.HeaderEdits ??= []).Add(new(name, value));
                    break;
                case "remove_header" when LuaReader.AsString(command[2]) is { } name:
                    (result.HeaderEdits ??= []).Add(new(name, null));
                    break;
                case "set_body":
                    result.Body = command[2] as byte[] ?? [];
                    break;
                case "deny":
                    result.Deny = new LuaDeny((int)(LuaReader.AsInt(command[2]) ?? 403), command[3] as byte[], Headers(command[4] as LuaTable));
                    break;
                case "cluster":
                    result.ClusterId = LuaReader.AsString(command[2]);
                    break;
                case "log":
                    Log(LuaReader.AsString(command[2]), LuaReader.AsString(command[3]));
                    break;
                case "metric" when LuaReader.AsString(command[2]) is { } kind && LuaReader.AsString(command[3]) is { } name:
                    if (!LuaMetrics.RecordUser(Name, kind, name, command[4] switch { long l => l, double d => d, _ => 1 },
                            command[5] as LuaTable))
                    {
                        Log("warn", $"metric '{name}' dropped: more than {LuaMetrics.MaxUserInstruments} script instruments or a kind clash");
                    }

                    break;
                case "timer" when acceptTimers && LuaReader.AsString(command[2]) is { } kind &&
                                  LuaReader.AsInt(command[3]) is { } delay && LuaReader.AsString(command[4]) is { } function:
                    StartTimer(kind == "every", TimeSpan.FromMilliseconds(delay), function, command[5]);
                    break;
            }
        }
    }

    private static List<KeyValuePair<string, string>>? Headers(LuaTable? table)
    {
        if (table is null)
        {
            return null;
        }

        var list = new List<KeyValuePair<string, string>>();
        foreach (var pair in table.Fields)
        {
            if (LuaReader.AsString(pair.Value) is { } value && IsHeaderSafe(pair.Key) && IsHeaderSafe(value))
            {
                list.Add(new(pair.Key, value));
            }
        }

        return list;
    }

    private static bool IsHeaderSafe(string value) => value.Length is > 0 and <= 16384 && value.IndexOfAny(['\r', '\n', '\0']) < 0;

    private void Log(string? level, string? message)
    {
        if (Logger is null || message is null)
        {
            return;
        }

        var logLevel = level switch
        {
            "debug" => LogLevel.Debug,
            "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Information,
        };
        Logger.Log(logLevel, "[lua:{Plugin}] {Message}", Name, message);
    }

    internal static long Micros(TimeSpan span) => Math.Max(1, (long)(span.TotalMilliseconds * 1000));

    internal static string Text(byte[]? bytes) => bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested)
        {
            return;
        }

        _disposed.Cancel();
        _pool?.Dispose();
        _callouts.Dispose();
    }
}
