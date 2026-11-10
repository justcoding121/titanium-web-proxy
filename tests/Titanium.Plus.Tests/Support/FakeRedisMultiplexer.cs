using System.Reflection;
using StackExchange.Redis;

namespace Titanium.Plus.Tests.Support;

/// <summary>
///     Tiny <see cref="IConnectionMultiplexer"/> stand-in for <see cref="Titanium.Plus.State.RedisFleetRateStore"/>
///     lease coverage on Windows Sonar (no TestCategory=Redis, no RESP handshake).
/// </summary>
internal static class FakeRedisMultiplexer
{
    public static IConnectionMultiplexer Create(
        out Func<int> evalCount,
        Func<RedisValue[], RedisResult>? evaluate = null,
        bool connected = true)
    {
        var count = 0;
        evalCount = () => Volatile.Read(ref count);
        var grant = evaluate ?? (_ => RedisResult.Create((RedisValue)1));
        var db = DispatchProxy.Create<IDatabase, DatabaseProxy>();
        ((DatabaseProxy)(object)db).Configure(
            () => Interlocked.Increment(ref count),
            grant);
        var mux = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
        ((MultiplexerProxy)(object)mux).Configure(connected, db);
        return mux;
    }

    // DispatchProxy.Create requires a public, non-sealed proxy type.
    public class MultiplexerProxy : DispatchProxy
    {
        private bool _connected;
        private IDatabase _db = null!;

        public void Configure(bool connected, IDatabase db)
        {
            _connected = connected;
            _db = db;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return targetMethod.Name switch
            {
                "get_IsConnected" => _connected,
                "GetDatabase" => _db,
                "Dispose" => null,
                "DisposeAsync" => ValueTask.CompletedTask,
                "Close" => null,
                "CloseAsync" => Task.CompletedTask,
                "get_Configuration" => "fake",
                "get_ClientName" => "fake",
                "get_TimeoutMilliseconds" => 1000,
                "get_OperationCount" => 0L,
                "get_IsConnecting" => false,
                "get_IncludeDetailInExceptions" or "set_IncludeDetailInExceptions"
                    or "get_PreserveAsyncOrder" or "set_PreserveAsyncOrder"
                    or "get_StormLogThreshold" or "set_StormLogThreshold" =>
                    targetMethod.ReturnType == typeof(bool) ? false :
                    targetMethod.ReturnType == typeof(int) ? 0 :
                    null,
                "GetEndPoints" => Array.Empty<System.Net.EndPoint>(),
                "GetServers" => Array.Empty<IServer>(),
                "GetStatus" when targetMethod.ReturnType == typeof(string) => "fake",
                "GetStatus" => null,
                "ResetStormLog" => null,
                "PublishReconfigure" => 0L,
                "PublishReconfigureAsync" => Task.FromResult(0L),
                "GetHashSlot" or "HashSlot" => 0,
                "AddLibraryNameSuffix" => null,
                "RegisterProfiler" => null,
                "Wait" => InvokeWait(targetMethod, args),
                "WaitAll" => InvokeWaitAll(args),
                "add_ErrorMessage" or "remove_ErrorMessage"
                    or "add_ConnectionFailed" or "remove_ConnectionFailed"
                    or "add_InternalError" or "remove_InternalError"
                    or "add_ConnectionRestored" or "remove_ConnectionRestored"
                    or "add_ConfigurationChanged" or "remove_ConfigurationChanged"
                    or "add_ConfigurationChangedBroadcast" or "remove_ConfigurationChangedBroadcast"
                    or "add_HashSlotMoved" or "remove_HashSlotMoved"
                    or "add_ServerMaintenanceEvent" or "remove_ServerMaintenanceEvent" => null,
                _ => throw new NotSupportedException("FakeRedisMultiplexer." + targetMethod.Name),
            };
        }

        private static object? InvokeWait(MethodInfo method, object?[]? args)
        {
            if (args is { Length: > 0 } && args[0] is Task task)
            {
                task.GetAwaiter().GetResult();
                if (method.ReturnType != typeof(void) && method.IsGenericMethod)
                {
                    return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
                }
            }

            return null;
        }

        private static object? InvokeWaitAll(object?[]? args)
        {
            if (args is { Length: > 0 } && args[0] is Task[] tasks)
            {
                Task.WaitAll(tasks);
            }

            return null;
        }
    }

    public class DatabaseProxy : DispatchProxy
    {
        private Action _onEval = static () => { };
        private Func<RedisValue[], RedisResult> _evaluate = static _ => RedisResult.Create((RedisValue)1);

        public void Configure(Action onEval, Func<RedisValue[], RedisResult> evaluate)
        {
            _onEval = onEval;
            _evaluate = evaluate;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name is "ScriptEvaluateAsync" or "ScriptEvaluate")
            {
                _onEval();
                RedisValue[] values = [];
                if (args is { Length: >= 3 } && args[2] is RedisValue[] redisValues)
                {
                    values = redisValues;
                }

                var result = _evaluate(values);
                if (targetMethod.ReturnType.IsGenericType &&
                    targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    return Task.FromResult(result);
                }

                return result;
            }

            return targetMethod.Name switch
            {
                "get_Database" => 0,
                "get_Multiplexer" => throw new NotSupportedException(),
                "IsConnected" => true,
                "Ping" => TimeSpan.Zero,
                "PingAsync" => Task.FromResult(TimeSpan.Zero),
                "TryWait" => false,
                "Wait" => null,
                "WaitAll" => null,
                _ => throw new NotSupportedException("FakeRedisDatabase." + targetMethod.Name),
            };
        }
    }
}
