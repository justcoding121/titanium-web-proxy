using System.Text;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Scripting.Wasm.Native;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// Every import the host provides: the proxy-wasm ABI 0.2.1 <c>env</c> functions, plus the 0.1.0 and 0.2.0 names that
/// older SDKs still import. A module that imports anything else fails to load with the import named.
/// </summary>
internal static unsafe partial class WasmImports
{
    public static readonly WasmHostFunction[] All = [.. ProxyFunctions()];

    private static List<WasmHostFunction> ProxyFunctions()
    {
        var list = new List<WasmHostFunction>();
        void Env(string name, string signature, WasmHostHandler handler) => list.Add(new("env", name, signature, true, handler));

        // Logging and time.
        Env("proxy_log", "iii", (vm, a) =>
        {
            vm.Pool.Log((ProxyLogLevel)a[0].I32, vm.ReadString(a[1].I32, a[2].I32));
            return WasmResult.Ok;
        });
        Env("proxy_get_log_level", "i", (vm, a) =>
        {
            vm.WriteI32(a[0].I32, (int)vm.Pool.LogLevel);
            return WasmResult.Ok;
        });
        Env("proxy_get_current_time_nanoseconds", "i", (vm, a) =>
        {
            vm.WriteU64(a[0].I32, (ulong)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100);
            return WasmResult.Ok;
        });
        Env("proxy_set_tick_period_milliseconds", "i", (vm, a) => vm.Pool.SetTickPeriod(vm, a[0].I32));

        // Buffers.
        Env("proxy_get_buffer_bytes", "iiiii", GetBufferBytes);
        Env("proxy_set_buffer_bytes", "iiiii", (vm, a) =>
        {
            var context = vm.Effective;
            if (context is null)
            {
                return WasmResult.BadArgument;
            }

            var data = vm.ReadBytes(a[3].I32, a[4].I32);
            return context.SetBuffer((ProxyBufferType)a[0].I32, a[1].I32, a[2].I32, data);
        });
        Env("proxy_get_buffer_status", "iii", (vm, a) =>
        {
            var buffer = vm.Effective?.GetBuffer((ProxyBufferType)a[0].I32);
            if (buffer is null)
            {
                return WasmResult.NotFound;
            }

            vm.WriteI32(a[1].I32, buffer.Length);
            vm.WriteU32(a[2].I32, vm.Effective is WasmHttpContext { BodyEnd: true } ? 1u : 0u);
            return WasmResult.Ok;
        });

        // Header maps.
        Env("proxy_get_header_map_pairs", "iii", (vm, a) =>
        {
            var map = MapOf(vm, a[0].I32);
            if (map is null)
            {
                return WasmResult.NotFound;
            }

            vm.ReturnBytes(map.Serialize(), a[1].I32, a[2].I32);
            return WasmResult.Ok;
        });
        Env("proxy_set_header_map_pairs", "iii", (vm, a) =>
        {
            var map = MapOf(vm, a[0].I32);
            if (map is null)
            {
                return WasmResult.NotFound;
            }

            var pairs = WasmHeaderMap.Deserialize(vm.Memory(a[1].I32, a[2].I32));
            if (pairs is null)
            {
                return WasmResult.ParseFailure;
            }

            map.SetAll(pairs);
            return WasmResult.Ok;
        });
        Env("proxy_get_header_map_value", "iiiii", (vm, a) =>
        {
            var map = MapOf(vm, a[0].I32);
            if (map is null)
            {
                return WasmResult.NotFound;
            }

            var value = map.Get(vm.ReadString(a[1].I32, a[2].I32));
            if (value is null)
            {
                return WasmResult.NotFound;
            }

            vm.ReturnBytes(Encoding.UTF8.GetBytes(value), a[3].I32, a[4].I32);
            return WasmResult.Ok;
        });
        Env("proxy_replace_header_map_value", "iiiii", (vm, a) =>
        {
            var map = MapOf(vm, a[0].I32);
            if (map is null)
            {
                return WasmResult.NotFound;
            }

            map.Replace(vm.ReadString(a[1].I32, a[2].I32), vm.ReadString(a[3].I32, a[4].I32));
            return WasmResult.Ok;
        });
        Env("proxy_add_header_map_value", "iiiii", (vm, a) =>
        {
            var map = MapOf(vm, a[0].I32);
            if (map is null)
            {
                return WasmResult.NotFound;
            }

            map.Add(vm.ReadString(a[1].I32, a[2].I32), vm.ReadString(a[3].I32, a[4].I32));
            return WasmResult.Ok;
        });
        Env("proxy_remove_header_map_value", "iii", (vm, a) =>
        {
            var map = MapOf(vm, a[0].I32);
            if (map is null)
            {
                return WasmResult.NotFound;
            }

            map.Remove(vm.ReadString(a[1].I32, a[2].I32));
            return WasmResult.Ok;
        });
        Env("proxy_get_header_map_size", "ii", (vm, a) =>
        {
            var map = MapOf(vm, a[0].I32);
            if (map is null)
            {
                return WasmResult.NotFound;
            }

            vm.WriteI32(a[1].I32, map.Serialize().Length);
            return WasmResult.Ok;
        });

        // Properties.
        Env("proxy_get_property", "iiii", (vm, a) =>
        {
            var context = vm.Effective;
            if (context is null)
            {
                return WasmResult.BadArgument;
            }

            var value = WasmProperties.Get(context, WasmProperties.NormalizePath(vm.ReadString(a[0].I32, a[1].I32)));
            if (value is null)
            {
                return WasmResult.NotFound;
            }

            vm.ReturnBytes(value, a[2].I32, a[3].I32);
            return WasmResult.Ok;
        });
        Env("proxy_set_property", "iiii", (vm, a) =>
        {
            var context = vm.Effective;
            if (context is null)
            {
                return WasmResult.BadArgument;
            }

            context.Properties[WasmProperties.NormalizePath(vm.ReadString(a[0].I32, a[1].I32))] = vm.ReadBytes(a[2].I32, a[3].I32);
            return WasmResult.Ok;
        });

        // Stream control.
        Env("proxy_send_local_response", "iiiiiiii", (vm, a) =>
        {
            var context = vm.Effective;
            if (context is null)
            {
                return WasmResult.BadArgument;
            }

            var details = vm.ReadString(a[1].I32, a[2].I32);
            var body = vm.ReadBytes(a[3].I32, a[4].I32);
            var headers = WasmHeaderMap.Deserialize(vm.Memory(a[5].I32, a[6].I32));
            if (headers is null)
            {
                return WasmResult.ParseFailure;
            }

            return context.SendLocalResponse(a[0].I32, details, body, headers, a[7].I32);
        });
        Env("proxy_continue_stream", "i", (vm, a) => vm.Effective?.ContinueStream((ProxyStreamType)a[0].I32) ?? WasmResult.BadArgument);
        Env("proxy_close_stream", "i", (vm, a) => vm.Effective?.CloseStream((ProxyStreamType)a[0].I32) ?? WasmResult.BadArgument);
        Env("proxy_continue_request", "", (vm, _) => vm.Effective?.ContinueStream(ProxyStreamType.HttpRequest) ?? WasmResult.BadArgument);
        Env("proxy_continue_response", "", (vm, _) => vm.Effective?.ContinueStream(ProxyStreamType.HttpResponse) ?? WasmResult.BadArgument);
        Env("proxy_clear_route_cache", "", (_, _) => WasmResult.Ok);
        Env("proxy_set_effective_context", "i", (vm, a) =>
        {
            if (!vm.TryGetContext(a[0].I32, out var context))
            {
                return WasmResult.BadArgument;
            }

            vm.Effective = context;
            return WasmResult.Ok;
        });
        Env("proxy_done", "", (_, _) => WasmResult.Ok);
        Env("proxy_get_configuration", "ii", (vm, a) =>
        {
            vm.ReturnBytes(vm.Effective?.Plugin.Configuration ?? [], a[0].I32, a[1].I32);
            return WasmResult.Ok;
        });
        Env("proxy_get_status", "iii", (vm, a) =>
        {
            vm.WriteI32(a[0].I32, 0);
            vm.ReturnBytes([], a[1].I32, a[2].I32);
            return WasmResult.Ok;
        });

        // Callouts, shared state, metrics, and foreign functions.
        foreach (var (name, signature) in new[]
                 {
                     ("proxy_http_call", "iiiiiiiiii"),
                     ("proxy_grpc_call", "iiiiiiiiiiii"),
                     ("proxy_grpc_stream", "iiiiiiiii"),
                     ("proxy_grpc_send", "iiii"),
                     ("proxy_grpc_cancel", "i"),
                     ("proxy_grpc_close", "i"),
                     ("proxy_get_shared_data", "iiiii"),
                     ("proxy_set_shared_data", "iiiii"),
                     ("proxy_register_shared_queue", "iii"),
                     ("proxy_resolve_shared_queue", "iiiii"),
                     ("proxy_dequeue_shared_queue", "iii"),
                     ("proxy_enqueue_shared_queue", "iii"),
                     ("proxy_call_foreign_function", "iiiiii"),
                     ("proxy_define_metric", "iiii"),
                     ("proxy_increment_metric", "iI"),
                     ("proxy_record_metric", "iI"),
                     ("proxy_get_metric", "ii"),
                 })
        {
            Env(name, signature, static (_, _) => WasmResult.Unimplemented);
        }

        return list;
    }

    private static int GetBufferBytes(WasmVm vm, WasmValRaw* a)
    {
        var buffer = vm.Effective?.GetBuffer((ProxyBufferType)a[0].I32);
        if (buffer is null)
        {
            return WasmResult.NotFound;
        }

        var start = a[1].I32;
        var max = a[2].I32;
        if (start < 0 || max < 0 || start > buffer.Length)
        {
            return WasmResult.BadArgument;
        }

        var length = Math.Min(max, buffer.Length - start);
        vm.ReturnBytes(buffer.AsSpan(start, length), a[3].I32, a[4].I32);
        return WasmResult.Ok;
    }

    private static WasmHeaderMap? MapOf(WasmVm vm, int type) => vm.Effective?.GetMap((ProxyMapType)type);

    internal static LogLevel ToLogLevel(ProxyLogLevel level) => level switch
    {
        ProxyLogLevel.Trace => LogLevel.Trace,
        ProxyLogLevel.Debug => LogLevel.Debug,
        ProxyLogLevel.Info => LogLevel.Information,
        ProxyLogLevel.Warn => LogLevel.Warning,
        ProxyLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };
}
