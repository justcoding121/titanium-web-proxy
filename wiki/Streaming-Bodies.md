# Streaming Bodies

By default Titanium relays request and response bodies as they flow, and only buffers a body in memory when you explicitly read it (`GetRequestBody()` / `GetResponseBody()`). For large downloads/uploads or endless streams (e.g. chunked server-sent events), buffering is undesirable or impossible.

This page covers three related capabilities that avoid buffering:

- [Modify a body as it streams](#modify-a-body-as-it-streams) — `OnRequestBodyWrite` / `OnResponseBodyWrite`
- [Generate a body as a stream](#generate-a-body-as-a-stream) — `RespondStreaming`
- [Draining bodies](#draining-bodies) — `DrainServerBodyAsync` / `DrainClientBodyAsync`

---

## Modify a body as it streams

Subscribe to `OnResponseBodyWrite` (or `OnRequestBodyWrite`). The handler is invoked once for each piece of the body as it streams through the proxy — reads are bounded by the buffer size, so memory stays flat regardless of the total body size.

```csharp
proxyServer.OnResponseBodyWrite += (sender, e) =>
{
    // e.BodyBytes is the current piece of the body.
    // Replace it to modify what the client receives.
    var text = Encoding.UTF8.GetString(e.BodyBytes);
    e.BodyBytes = Encoding.UTF8.GetBytes(text.Replace("http://", "https://"));

    return Task.CompletedTask;
};
```

### `BeforeBodyWriteEventArgs`

| Member | Description |
| --- | --- |
| `BodyBytes` | The current piece of the body. Assign a new array to change what is written. |
| `IsChunked` | Whether the body is being written as a stream of chunks. |
| `IsLastChunk` | `true` on the final piece. Set it to `true` yourself to stop writing further pieces (the stream is terminated). |
| `Session` | The owning `SessionEventArgs`. |

### Important notes

- **Do not combine with `GetResponseBody()` / `GetRequestBody()`.** Those buffer the entire body; if you read the body, the streaming hook is bypassed. Choose one approach per message.
- **Bytes are exposed as they appear on the wire.** If the message uses `Content-Encoding` (gzip/br/deflate), `BodyBytes` contains the still-compressed bytes. The proxy does not decompress/recompress inside the hook, in order to preserve exact framing and length. Decode yourself if needed.
- **Fixed-length bodies:** if the response has a `Content-Length` (not chunked), you may modify bytes but should not change the total length, since the header was already sent. For chunked bodies the length may vary freely.
- **Transport support:**
  - HTTP/1.x: the per-piece hook runs for plain and TLS-decrypted connections (any `NetworkStream` / `SslStream`-backed transport).
  - HTTP/2: the hook runs per DATA frame, including over TLS (on by default via `ProxyServer.EnableHttp2`; set `false` to force HTTP/1.1 only).
  - HTTP/3: native H3→H3 fires `OnRequestBodyWrite` / `OnResponseBodyWrite` per DATA frame after `BeforeRequest` (see [Protocol Support](Protocol-Support)). H3→TCP fallback still buffers the request body up to `MaxBufferedBodyBytes`. For endless H3 uploads that you short-circuit with `Ok`/`Respond`, the unread request half is aborted rather than drained.

---

## Generate a body as a stream

Use `RespondStreaming` to answer the client with a body you produce on the fly, without ever holding the whole body in memory. This is ideal for serving a large file or a synthetic, potentially endless stream.

The proxy chooses framing from the response headers:

- If you set a **`Content-Length`**, the body is written raw and you must write exactly that many bytes.
- Otherwise the response is **chunked** (HTTP/1.x) or sent as **DATA frames** (HTTP/2).

### Stream a large file (fixed length)

```csharp
proxyServer.BeforeRequest += (sender, e) =>
{
    var path = "/path/to/large.bin";

    var response = new Response
    {
        StatusCode = 200,
        StatusDescription = "OK",
        HttpVersion = e.HttpClient.Request.HttpVersion
    };
    response.Headers.AddHeader(KnownHeaders.ContentType, "application/octet-stream");
    response.Headers.AddHeader(KnownHeaders.ContentLength, new FileInfo(path).Length.ToString());

    e.RespondStreaming(response, async (stream, ct) =>
    {
        await using var file = File.OpenRead(path);
        await file.CopyToAsync(stream, ct); // streamed; never fully loaded into memory
    });

    return Task.CompletedTask;
};
```

### Stream an endless/chunked body

Omit `Content-Length` and just keep writing; each write becomes a chunk (or DATA frame):

```csharp
proxyServer.BeforeRequest += (sender, e) =>
{
    var response = new Response
    {
        StatusCode = 200,
        StatusDescription = "OK",
        HttpVersion = e.HttpClient.Request.HttpVersion
    };
    response.Headers.AddHeader(KnownHeaders.ContentType, "text/event-stream");

    e.RespondStreaming(response, async (stream, ct) =>
    {
        while (!ct.IsCancellationRequested)
        {
            var line = Encoding.UTF8.GetBytes($"data: {DateTime.UtcNow:o}\n\n");
            await stream.WriteAsync(line, 0, line.Length, ct);
            await Task.Delay(1000, ct);
        }
    }, closeServerConnection: true);

    return Task.CompletedTask;
};
```

The delegate receives a write-only `Stream`; only one buffer is in flight at a time, so memory stays bounded regardless of total size.

> **HTTP/2 note:** `RespondStreaming` runs on a background task so other multiplexed streams on the same
> connection keep making progress. An *endless* synthetic body can still starve the **connection-level**
> flow-control window (inherent to HTTP/2); reservation waits are best-effort (timeout). For finite bodies
> this is not a concern. Do not await the body itself inside `BeforeRequest` — return after calling
> `RespondStreaming` so the handler does not block header dispatch.

---

## Draining bodies

When you short-circuit a message (custom response, block, redirect, or streaming override), the *other* side may still have an unread body sitting on its TCP connection. Draining reads and discards those bytes (without buffering them) so the connection can be reused.

```csharp
// Read and discard the unread server response body (keeps the server connection reusable).
await e.DrainServerBodyAsync();

// Read and discard the unread client request body (keeps the client keep-alive connection reusable).
await e.DrainClientBodyAsync();
```

Both are no-ops if the body was already received or the message has no body. The proxy already drains automatically on the normal `Respond`/`RespondStreaming` paths, so these are for advanced/manual control.

### Drain vs. close

An HTTP/1.1 connection cannot be both reused *and* have its body skipped. You have two choices:

- **Drain** (default for `Respond`/`RespondStreaming`): read and discard the body so the connection is reusable. Uses bounded memory even for very large bodies.
- **Close**: pass `closeServerConnection: true` to `Respond`/`RespondStreaming`, or call `e.TerminateServerConnection()`, to close the connection instead of reading its body.

> **Warning:** draining an *endless* chunked body (one that never sends its terminating zero chunk) blocks until the cancellation token fires or the connection closes. For such responses, close the connection instead of draining.

---

## See also

- [Home](Home) — overview of all major features
- [API documentation](https://justcoding121.github.io/titanium-web-proxy/docs/api/Titanium.Web.Proxy.ProxyServer.html)
