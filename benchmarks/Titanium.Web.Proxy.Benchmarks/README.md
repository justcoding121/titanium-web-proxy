# Titanium.Web.Proxy.Benchmarks

Measurement harness required by the hardening plan's "Measurement prerequisite": nothing in the
repository measured throughput or allocation before this project existed, yet several later plan
items (the HTTP/2 proxy-owned concurrency default, the graduation gates) depend on real numbers
rather than guesses.

## Running

This project is **not** run per-PR or in CI. Run it manually, from Release, at phase boundaries:

```powershell
dotnet run -c Release --project benchmarks/Titanium.Web.Proxy.Benchmarks -- --filter '*'
```

Pass `--filter '*ClassName*'` to run a single benchmark class while iterating. It is included in
`src/Titanium.Web.Proxy.sln` only so it keeps compiling under `--warnaserror`; a benchmark class
that no longer builds against the current internal APIs is a signal worth seeing immediately, not
after it has silently rotted for several releases.

## Scenario coverage

| Plan scenario | Benchmark class | Notes |
|---|---|---|
| Header parse cost | `HeaderParseBenchmarks` | Real internal `HeaderParser.ReadHeaders`, 5/25/100 headers. |
| Chunk parse cost | `ChunkSizeLineParseBenchmarks` | Real line reader + today's `LimitedStream` hex-parse baseline. Update alongside the Phase B grammar-conformant chunk parser so the before/after comparison stays valid. |
| HTTP/1 throughput, with/without body interception | `Http1ProxyThroughputBenchmarks` | Real `ProxyServer` against a plain-HTTP loopback origin; `InterceptBody` toggles `GetRequestBody()`/`GetResponseBody()`. |
| HTTP/2 multiplexed streams at varying concurrency | `Http2ProxyThroughputBenchmarks` | Real `ProxyServer` MITM-decrypting to a Kestrel HTTP/2 origin; `ConcurrentStreams` = 1/10/50 over one connection. |
| Allocation / LOH growth per request | All of the above | `[MemoryDiagnoser]` on every benchmark class reports Gen0/1/2 collections and allocated bytes per operation. |
| Connection-pool acquire/release under contention | Not isolated separately | `Http1ProxyThroughputBenchmarks`'s repeated GETs against one loopback origin already drive `TcpConnectionFactory.GetServerConnection`/`Release` on every iteration. Constructing a `SessionEventArgsBase` by hand to isolate the pool alone was judged not worth the fragility versus this incidental, realistic coverage; revisit if pool-lock sharding (PR item 7) needs a dedicated number. |

## Certificates

`Support/LoopbackCertificateAuthority` mints a process-local root and a `localhost` leaf entirely
in memory, used only to give the HTTP/2 benchmark's client and server legs something to validate.
Nothing here is persisted or trusted system-wide; it has no relationship to a real deployment's
certificate store, and must not be reused for anything beyond this harness.
