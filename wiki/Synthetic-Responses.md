# Synthetic Responses

Answer the client directly from a handler without contacting the origin, or replace an origin response entirely. Use **`e.Respond(ProxyResults.*)`** as the entry point for synthetic (locally generated) responses.

This page covers:

- [When to synthesize](#when-to-synthesize)
- [Buffered responses](#buffered-responses)
- [Streamed responses](#streamed-responses)
- [Migration from legacy APIs](#migration-from-legacy-apis)
- [Common pitfalls](#common-pitfalls)

For modifying an origin response in place (chunk-by-chunk edits), see [Streaming Bodies](Streaming-Bodies).

---

## When to synthesize

| Hook | Effect |
| --- | --- |
| `BeforeRequest` | Skip the origin entirely and answer from the proxy |
| `BeforeResponse` | Replace the response after headers were received from the origin |
| `AfterResponse` | Too late — the response was already sent to the client |

**Decision tree:**

1. Do you need to **edit** the origin body? → Use `GetResponseBody` / `SetResponseBody` or `OnResponseBodyWrite` ([Streaming Bodies](Streaming-Bodies)).
2. Do you need to **generate** a new response? → Use `ProxyResults` below.
3. Is the body **small and fits in memory**? → Buffered factories (`Html`, `Json`, `WithStatus`, …).
4. Is the body **large or unbounded**? → Streaming factories (`File`, `Stream`).

---

## Buffered responses

Build a `Response` with `ProxyResults`, then pass it to `e.Respond(...)`:

```csharp
proxyServer.BeforeRequest += (sender, e) =>
{
    if (ShouldBlock(e))
        e.Respond(ProxyResults.Json(new { error = "blocked" }, HttpStatusCode.Forbidden));

    return Task.CompletedTask;
};
```

### HTML block page

```csharp
e.Respond(ProxyResults.Html("<html><body>Blocked</body></html>"));
```

Sets `Content-Type: text/html; charset=utf-8`.

### JSON API denial

```csharp
e.Respond(ProxyResults.Json(new { error = "denied" }, HttpStatusCode.Forbidden));
```

Uses `System.Text.Json`. For NativeAOT / source generation, pass a `JsonTypeInfo<T>`. For Newtonsoft.Json, serialize manually and use `ProxyResults.Text(json)`.

### Plain text / any status

```csharp
e.Respond(ProxyResults.WithStatus(HttpStatusCode.NotFound, "Not found"));
e.Respond(ProxyResults.NoContent());
```

### Raw bytes

```csharp
e.Respond(ProxyResults.Bytes(pngBytes, "image/png"));
```

### Redirect (any status)

```csharp
e.Respond(ProxyResults.Redirect("https://safe.example/", HttpStatusCode.MovedPermanently));
```

Default is 302 Found. Use 307 or 308 to preserve the request method.

---

## Streamed responses

For large files or unbounded streams, use `ProxyResults.File` or `ProxyResults.Stream`. Both return a `StreamingProxyResult` consumed by `e.RespondStreaming(...)`:

```csharp
// Serve a cached file without buffering it in memory
e.RespondStreaming(ProxyResults.File(@"C:\cache\large.bin", "application/octet-stream"), closeServerConnection: false);

// Custom stream (e.g. server-sent events)
e.RespondStreaming(ProxyResults.Stream(
    HttpStatusCode.OK,
    "text/event-stream",
    async (stream, ct) =>
    {
        await stream.WriteAsync("data: hello\n\n"u8.ToArray(), ct);
    }), closeServerConnection: false);

// Fixed-length stream — set contentLength to avoid chunked framing
e.Respond(ProxyResults.Stream(
    HttpStatusCode.OK,
    "application/octet-stream",
    contentLength: fileInfo.Length,
    writeBody: async (stream, ct) => { /* write exactly contentLength bytes */ }));
```

See [Streaming Bodies — Generate a body as a stream](Streaming-Bodies#generate-a-body-as-a-stream) for framing details (Content-Length vs chunked / HTTP/2 DATA frames).

### Range request limitation

`ProxyResults.File` always returns the **full file with status 200**. HTTP `Range:` request headers are **not** handled. This works for `HttpClient`, `curl`, and `wget`, but may fail for:

- Browser `<video>` / `<audio>` tags that require 206 Partial Content for seeking
- Download managers that resume partial downloads

For Range support, use `ProxyResults.Stream` (or `RespondStreaming` directly) with custom range logic.

---

## Migration from legacy APIs

| Legacy | Preferred |
| --- | --- |
| `e.Ok(html)` | `e.Respond(ProxyResults.Html(html))` — `Ok` still works and now sets `Content-Type: text/html` |
| `e.GenericResponse(body, status)` | `e.Respond(ProxyResults.WithStatus(status, body))` |
| `e.Redirect(url)` | `e.Respond(ProxyResults.Redirect(url))` — supports custom status codes |
| Manual `RespondStreaming` setup | `e.RespondStreaming(ProxyResults.Stream(...))` or `e.RespondStreaming(ProxyResults.File(...))` |
| `e.Respond(new Response { ... })` | Still valid for advanced/custom cases |

---

## Common pitfalls

- **Calling `Respond` after the response was sent** throws `InvalidOperationException`. Only call from `BeforeRequest` or `BeforeResponse`.
- **Replacing a response after the origin replied** — the original server body is drained so the connection can be reused. For large origin bodies you don't want to read, pass `closeServerConnection: true` or call `e.TerminateServerConnection()`.
- **Do not buffer large bodies through `Html` / `Json`** — use `File` or `Stream` instead.
- **`AfterResponse` cannot synthesize** — use `BeforeRequest` or `BeforeResponse`.
