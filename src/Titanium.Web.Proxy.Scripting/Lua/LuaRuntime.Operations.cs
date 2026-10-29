using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Scripting.Lua;

internal sealed partial class LuaRuntime
{
    /// <summary>Timers one plugin may have scheduled at once; later ones are dropped with a warning.</summary>
    public const int MaxTimers = 256;

    private int _timers;

    /// <summary>Timers scheduled and not yet finished.</summary>
    public int ActiveTimers => Volatile.Read(ref _timers);

    /// <summary>Host operations a script may wait on. Null means the operation is not available.</summary>
    private async partial ValueTask<byte[]?> RunOperationAsync(string? kind, LuaTable? op, CancellationToken token)
    {
        switch (kind)
        {
            case "sleep":
                var ms = Math.Clamp(op?.Int("ms") ?? 0, 0, 60_000);
                await Task.Delay(TimeSpan.FromMilliseconds(ms), token).ConfigureAwait(false);
                return NoValues;
            case "http" when op is not null:
                return await _callouts.CallAsync(op, token).ConfigureAwait(false);
            case "jwt" when op is not null:
                return _jwt.Verify(op);
            default:
                return null;
        }
    }

    /// <summary>
    /// Runs the global function <paramref name="function"/> after <paramref name="delay"/> (and again every
    /// <paramref name="delay"/> when <paramref name="every"/>), with <c>ctx.arg</c> set to <paramref name="arg"/>. Timers stop
    /// when the plugin is disposed (reload or shutdown); a reloaded plugin starts its own from <c>on_configure</c>.
    /// </summary>
    private void StartTimer(bool every, TimeSpan delay, string function, object? arg)
    {
        if (_disposed.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.Increment(ref _timers) > MaxTimers)
        {
            Interlocked.Decrement(ref _timers);
            Log("warn", $"timer for {function} dropped: {MaxTimers} timers already scheduled");
            return;
        }

        var writer = new LuaWriter();
        var mark = writer.BeginTable();
        writer.Field("phase", "timer");
        writer.Field("plugin", Name);
        writer.Field("timer", function);
        writer.String("arg");
        writer.Value(arg);
        writer.EndTable(mark, 4);
        var snapshot = writer.ToArray();

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    await Task.Delay(delay, Stopping).ConfigureAwait(false);
                    try
                    {
                        await CallAsync(function, snapshot, Stopping).ConfigureAwait(false);
                    }
                    catch (LuaScriptException ex)
                    {
                        Logger?.LogWarning("Lua plugin '{Plugin}' timer {Function} failed: {Error}", Name, function, ex.Message);
                    }
                }
                while (every);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _timers);
            }
        }, Stopping);
    }
}
