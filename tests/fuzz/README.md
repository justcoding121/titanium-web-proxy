# Parser fuzz corpus

Deterministic corpus for the two parsers that read bytes from an untrusted peer before any policy runs:

- the **HTTP/1 request path**: request line, `HeaderParser` with its header budget, `Http1FramingValidator`, and the
  chunked body relay (`ChunkSizeParser`, `ChunkedTrailerHelper`);
- **`ClientHelloSniParser`**: the TLS ClientHello reader used for SNI routing on passthrough listeners.

The goal is an invariant, not a particular error message: **an ambiguous request is never forwarded as received.** It
is rejected, or normalized to one unambiguous framing, and nothing crashes or hangs.

## Run it

```powershell
dotnet test tests/Titanium.Web.Proxy.UnitTests/Titanium.Web.Proxy.UnitTests.csproj -c Release --filter "FullyQualifiedName~ParserCorpusRunnerTests"
```

The corpus is copied next to the test assembly (`fuzz/corpus/**`). The same job runs on every pull request that touches
the parsers (`.github/workflows/conformance.yml`, job `parser-and-chaos`).

What the runner does (`tests/Titanium.Web.Proxy.UnitTests/Fuzz/ParserCorpusRunnerTests.cs`):

1. **Coverage guard**: every ambiguity class below has at least one seed.
2. **Seeds**: each `.case` / `.hex` file meets the expectation written in its header.
3. **Mutations**: each seed is truncated at every prefix (sampled for large seeds) and then mutated with a fixed-seed
   `Random` (replace, delete, insert or duplicate bytes, favoring CR, LF, NUL, colon, space and digits). The same inputs
   run every time, so a failure reproduces from the log line alone.
   Every mutant must stay inside the invariants, finish within a 10 s deadline, and throw only a documented rejection.

## Seed classes (`corpus/http1-request`)

| Prefix | What it covers |
|---|---|
| `cl-te-` | Content-Length together with Transfer-Encoding, in both orders and with odd casing |
| `te-cl-` | Transfer-Encoding forms that a different parser might read differently from us (`xchunked`, a second line, ...) |
| `dup-cl-` | Duplicate or conflicting Content-Length (identical values collapse, differing values are rejected) |
| `obs-fold-` | Obsolete line folding in headers |
| `bare-lf-` | Bare LF line endings in the request line, headers and chunk framing |
| `space-before-colon-` | Whitespace between a header name and the colon |
| `oversized-` | Header lines, counts and aggregates over the budget |
| `invalid-chunk-` | Bad chunk sizes, missing CRLF, data longer or shorter than declared |

Other seeds (`te-*`, `request-line-*`, `nul-in-header-value`, ...) are individual edge cases. `corpus/client-hello`
holds binary ClientHello records (length-field overflows, truncations, hostile server names).

## Seed file formats

`.case` (HTTP/1): lines starting with `# name: value` are directives; every other line is payload, concatenated with no
separator. Payload escapes: `\r \n \t \0 \\ \xNN`, and `%REPEAT(text,n)%` (nestable) for large inputs. Escapes keep the
bytes exact whatever `core.autocrlf` does to the file.

| Directive | Meaning |
|---|---|
| `expect: reject` | must throw a documented rejection (`reject-type:` pins the exception type) |
| `expect: normalize` | must be accepted, and the `assert-*` directives describe the single framing that results |
| `expect: incomplete` | the headers parse but the body is cut short: it must not be relayed as a complete message |
| `expect: accept` | must be accepted unchanged |
| `reason:` | why the expectation holds (free text) |
| `assert-header: N=V` / `assert-no-header` / `assert-header-count: N=n` | assertions on the parsed headers |
| `assert-body-complete` | the body relay ends cleanly |

`.hex` (ClientHello): whitespace-separated hex bytes, `#` comments, and `# expect: found:<name> | nosni | nottls |
needmore | any`.

## The fuzz entry point

`ParserFuzzTarget` (in the unit-test project) exposes the two entry points an external fuzzer calls:

```csharp
ParserFuzzTarget.Http1Request(byte[] data);   // throws FuzzInvariantException on an invariant violation
ParserFuzzTarget.ClientHello(byte[] data);    // same
```

Anything that is not a documented rejection (`FormatException`, `Http1FramingException`, `ProxyHttpException`,
`HeaderLimitsExceededException`, `EndOfStreamException`, `IOException`) is reported as a violation. SharpFuzz is optional
and not a dependency. To use it, build a small console project that references the unit-test project (or links these
files) and calls `Fuzzer.OutOfProcess.Run(ParserFuzzTarget.Http1Request)` under `afl-fuzz`, with `corpus/` as the input
directory after expanding the `.case` seeds to raw bytes (`FuzzCorpus.Load` does this). Run that locally or in a
nightly job; the deterministic runner is the gate.

## proxy-wasm host-call corpus

A second pair of corpus folders covers the bytes a WebAssembly guest hands the host, which is the one place a plugin
author's bug (or a hostile module) reaches Titanium code with a length it chose:

| Folder | Entry point | What it checks |
|---|---|---|
| `corpus/proxy-wasm-header-map` (`.hex`) | `WasmHostCallFuzzTarget.HeaderMap` | The serialized header map behind `proxy_set_header_map_pairs`, `proxy_send_local_response` and `proxy_http_call`: malformed input decodes to "no map" (the host answers `ParseFailure`), never throws, and a decoded map serializes back to the same pairs and reports no edits until the guest changes it. |
| `corpus/proxy-wasm-property-path` (`.case`) | `WasmHostCallFuzzTarget.PropertyPath` | The path behind `proxy_get_property` and `proxy_set_property`: NUL-separated or dotted, never throws, a normalized path has no NUL and no trailing dot, and an unknown or malformed path is `NotFound`. |

Header-map seeds use `# expect: parsed:N | malformed | any`; path seeds use `# expect: notfound | any` and optionally
`# expect-http: <text>` for the value the HTTP property table must return. Seed prefixes for the header map are
`valid-`, `short-`, `count-`, `length-`, `truncated-` and `utf8-`; the runner fails if one has no seed.

```powershell
dotnet test tests/Titanium.Web.Proxy.Scripting.Tests/Titanium.Web.Proxy.Scripting.Tests.csproj -c Release --filter "FullyQualifiedName~WasmHostCallCorpusTests"
```

No Wasmtime or Lua native is needed: the decoder and the property table are plain managed code. The mutation pass is the
same shape as above (every prefix, then fixed-seed edits favoring NUL, 0xFF, 0x7F and length-field bytes). To fuzz it
with SharpFuzz, link `WasmHostCallFuzzTarget.cs` into a console project that references the scripting assembly and
call `Fuzzer.OutOfProcess.Run(WasmHostCallFuzzTarget.HeaderMap)` (or `.PropertyPath`) under `afl-fuzz`.

## Adding a seed

1. Write the `.case` file with the right prefix and a `reason:`.
2. Run the filter above. A new seed that fails is either a parser bug (fix it, keep the seed) or a wrong expectation
   (fix the header). Do not loosen an expectation to make a seed pass.

## What the corpus found

The first runs exposed four real gaps, fixed in the same change: a bare CR or NUL inside a header value, an empty header
name (which used to surface as an `ArgumentException`), whitespace inside a header name, and an empty chunk-trailer name.
Each is now a documented `FormatException` / `ProxyHttpException`.

## Known gaps (documented, not fixed here)

- **Malformed origin header line in the transparent/reverse path** (no colon) raises `FormatException`. The client
  connection closes without a 502 because the origin-failure answer only covers socket-level errors.
- **EOF inside a header block** is treated as the end of the headers, so a request cut off mid-headers parses as a
  complete (shorter) request. `no-terminating-blank-line` pins the current behavior. A body cut short is covered
  separately (`invalid-chunk-truncated-body`, `invalid-chunk-missing-last-chunk`) and is never relayed as complete.
- **Surplus bytes after chunk data** are consumed as the trailer line and the relay re-frames the chunk. This is a
  normalization (the origin sees well-formed chunks), pinned by `invalid-chunk-data-longer-than-size`.
