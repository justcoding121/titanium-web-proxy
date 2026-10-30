using System.Net;
using System.Text;
using System.Text.Json;

namespace Titanium.Manager.Api;

/// <summary>Bounded body reads for manager admin, agent, and UI JSON helpers (8 MiB, same as Plus control plane).</summary>
internal static class ManagerRequestBody
{
    /// <summary>Matches Plus <c>ControlHttpServer.MaxBodyBytes</c>.</summary>
    internal const int MaxBodyBytes = 8 * 1024 * 1024;

    internal static async Task<byte[]> ReadBytesAsync(HttpListenerContext context)
    {
        var declared = context.Request.ContentLength64;
        if (declared > MaxBodyBytes)
        {
            throw new RequestTooLargeException();
        }

        await using var ms = new MemoryStream(declared > 0 ? (int)Math.Min(declared, MaxBodyBytes) : 0);
        var buffer = new byte[8192];
        while (true)
        {
            var read = await context.Request.InputStream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (ms.Length + read > MaxBodyBytes)
            {
                throw new RequestTooLargeException();
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    internal static async Task<string> ReadUtf8Async(HttpListenerContext context) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(context).ConfigureAwait(false));

    internal static async Task<JsonDocument> ReadJsonAsync(HttpListenerContext context)
    {
        var json = await ReadUtf8Async(context).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }
}

internal sealed class RequestTooLargeException : Exception;
