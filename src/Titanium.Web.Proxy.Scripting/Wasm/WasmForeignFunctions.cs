using System.Collections.Concurrent;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>A host function a WASM plugin calls by name with <c>proxy_call_foreign_function</c>.</summary>
/// <param name="arguments">The bytes the guest passed.</param>
/// <returns>The bytes handed back to the guest.</returns>
public delegate byte[] WasmForeignFunction(ReadOnlySpan<byte> arguments);

/// <summary>
/// The functions WASM plugins can reach through <c>proxy_call_foreign_function</c>, shared by every plugin in the process.
/// <c>compress</c> and <c>uncompress</c> (zlib, as in Envoy) are built in. A function runs on the guest's call with its VM
/// locked, so it must be quick and must not block; an exception it throws returns InternalFailure to the guest.
/// </summary>
public static class WasmForeignFunctions
{
    /// <summary>Largest output the built-in <c>uncompress</c> produces, so a small input cannot inflate without bound.</summary>
    public const int MaxUncompressedBytes = 16 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, WasmForeignFunction> Functions = new(StringComparer.Ordinal)
    {
        ["compress"] = Compress,
        ["uncompress"] = Uncompress,
    };

    /// <summary>Adds <paramref name="function"/> as <paramref name="name"/>, replacing any function of that name.</summary>
    public static void Register(string name, WasmForeignFunction function)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(function);
        Functions[name] = function;
    }

    /// <summary>Removes the function named <paramref name="name"/>. Returns false when there was none.</summary>
    public static bool Unregister(string name) => Functions.TryRemove(name, out _);

    internal static bool TryGet(string name, out WasmForeignFunction function) => Functions.TryGetValue(name, out function!);

    private static byte[] Compress(ReadOnlySpan<byte> input)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] Uncompress(ReadOnlySpan<byte> input)
    {
        using var source = new MemoryStream(input.ToArray());
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = zlib.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxUncompressedBytes)
            {
                throw new InvalidDataException($"uncompressed data exceeds {MaxUncompressedBytes} bytes");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
