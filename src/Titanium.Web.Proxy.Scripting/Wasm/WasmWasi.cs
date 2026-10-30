using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Titanium.Web.Proxy.Scripting.Wasm.Native;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// The WASI preview1 subset proxy-wasm SDKs import (Rust, Go, C++ toolchains link these even when the filter never touches
/// a file). No arguments, no environment, no files: stdout and stderr go to the plugin's log, clocks and randomness are
/// real, <c>proc_exit</c> traps (a command module that exits 0 from <c>_start</c> still loads), and every file call answers
/// EBADF. Defined under both <c>wasi_snapshot_preview1</c> and the older <c>wasi_unstable</c>.
/// </summary>
internal static unsafe class WasmWasi
{
    public const int Success = 0;
    public const int BadFileDescriptor = 8;
    public const int Fault = 21;
    public const int Invalid = 28;
    public const int Io = 29;
    public const int NotSupported = 52;

    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    public static void AddTo(List<WasmHostFunction> list)
    {
        foreach (var module in new[] { "wasi_snapshot_preview1", "wasi_unstable" })
        {
            void Wasi(string name, string signature, WasmHostHandler handler) =>
                list.Add(new(module, name, signature, true, handler, Io, Fault));

            Wasi("args_sizes_get", "ii", EmptySizes);
            Wasi("args_get", "ii", static (_, _) => Success);
            Wasi("environ_sizes_get", "ii", EmptySizes);
            Wasi("environ_get", "ii", static (_, _) => Success);
            Wasi("clock_time_get", "iIi", static (vm, a) =>
            {
                if (!TryNow(a[0].I32, out var nanoseconds))
                {
                    return Invalid;
                }

                vm.WriteU64(a[2].I32, nanoseconds);
                return Success;
            });
            Wasi("clock_res_get", "ii", static (vm, a) =>
            {
                if (a[0].I32 is < 0 or > 3)
                {
                    return Invalid;
                }

                vm.WriteU64(a[1].I32, a[0].I32 == 0 ? 100UL : (ulong)(1_000_000_000.0 / Stopwatch.Frequency));
                return Success;
            });
            Wasi("random_get", "ii", static (vm, a) =>
            {
                RandomNumberGenerator.Fill(vm.Memory(a[0].I32, a[1].I32));
                return Success;
            });
            Wasi("fd_write", "iiii", FdWrite);
            Wasi("fd_read", "iiii", static (_, _) => BadFileDescriptor);
            Wasi("fd_close", "i", static (_, _) => BadFileDescriptor);
            Wasi("fd_seek", "iIii", static (_, _) => BadFileDescriptor);
            Wasi("fd_fdstat_get", "ii", static (_, _) => BadFileDescriptor);
            Wasi("fd_prestat_get", "ii", static (_, _) => BadFileDescriptor);
            Wasi("fd_prestat_dir_name", "iii", static (_, _) => BadFileDescriptor);
            Wasi("path_open", "iiiiiIIii", static (_, _) => BadFileDescriptor);
            Wasi("poll_oneoff", "iiii", static (_, _) => NotSupported);
            Wasi("sched_yield", "", static (_, _) => Success);
            list.Add(new(module, "proc_exit", "i", false, static (vm, a) =>
            {
                vm.RecordExit(a[0].I32);
                throw new WasmTrapException($"proc_exit({a[0].I32})");
            }));
        }
    }

    private static int EmptySizes(WasmVm vm, WasmValRaw* a)
    {
        vm.WriteU32(a[0].I32, 0);
        vm.WriteU32(a[1].I32, 0);
        return Success;
    }

    private static bool TryNow(int clock, out ulong nanoseconds)
    {
        switch (clock)
        {
            case 0:
                nanoseconds = (ulong)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100;
                return true;
            case 1:
            case 2:
            case 3:
                nanoseconds = (ulong)(Stopwatch.GetElapsedTime(StartTimestamp).Ticks * 100);
                return true;
            default:
                nanoseconds = 0;
                return false;
        }
    }

    /// <summary>Writes stdout (info) and stderr (error) to the plugin's log. Other descriptors answer EBADF.</summary>
    private static int FdWrite(WasmVm vm, WasmValRaw* a)
    {
        var fd = a[0].I32;
        if (fd is not (1 or 2))
        {
            return BadFileDescriptor;
        }

        var iovs = a[1].I32;
        var count = a[2].I32;
        if (count < 0 || count > 1024)
        {
            return Invalid;
        }

        var text = new StringBuilder();
        long written = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = vm.Memory(iovs + (i * 8), 8);
            var pointer = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            text.Append(vm.ReadString(pointer, length));
            written += length;
        }

        var line = text.ToString().TrimEnd('\n', '\r');
        if (line.Length > 0)
        {
            vm.Pool.Log(fd == 1 ? ProxyLogLevel.Info : ProxyLogLevel.Error, line);
        }

        vm.WriteU32(a[3].I32, (uint)Math.Min(written, uint.MaxValue));
        return Success;
    }
}
