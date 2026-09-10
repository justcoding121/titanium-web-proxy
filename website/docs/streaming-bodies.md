# Streaming bodies

Titanium can stream large uploads and downloads instead of buffering entire payloads — across HTTP/1.x, HTTP/2, and HTTP/3.

Full guidance, limitations, and examples: [Streaming-Bodies wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Streaming-Bodies). Synthetic / custom responses: [Synthetic-Responses wiki](https://github.com/justcoding121/titanium-web-proxy/wiki/Synthetic-Responses).

## When to stream

- Large uploads / downloads
- Proxied media
- Hooks that transform data incrementally (`OnRequestBodyWrite` / `OnResponseBodyWrite`)

## Library hooks

> **For .NET library users** — use session events and body-write callbacks on `SessionEventArgs` / related types. Prefer streaming APIs when you do not need the full buffer.

For HTTP/3, per-chunk streaming hooks are part of the experimental surface (`TWP001`).
