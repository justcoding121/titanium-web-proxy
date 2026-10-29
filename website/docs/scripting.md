# Scripting

Write a route filter in Lua 5.4 or ship one as a WebAssembly module, and run it behind the same `middleware:` list as a .NET plugin. This page is for operators and plugin authors who want to edit or add a filter without building a .NET assembly.

The decision, the cost budgets, and the safety rules are on [Scripting engines](/docs/plugins#scripting-engines) in Per-route plugins. This page covers what you install, how a script is held in check, what a script can call, and how the engines compare with other gateways.

## Dependencies

Each engine needs one native library beside the `titanium` binary. A computer without it keeps running; only a configuration that names that kind of plugin fails to load, and the message says which file is missing.

| Engine | Native library | What it is |
| --- | --- | --- |
| `kind: lua` | `lua54.dll` / `liblua54.so` / `liblua54.dylib` | Lua 5.4.7 built together with the Titanium host shim (`ctx` and the libraries below). A stock `lua` package from a distribution or Homebrew does not work. |
| `kind: wasm` | `wasmtime.dll` / `libwasmtime.so` / `libwasmtime.dylib` | The Wasmtime 50.0.0 C API, a prebuilt release archive. |

The release archive for your runtime identifier (RID) carries both files next to the binary. Check what loads with:

```text
titanium scripting-deps status
```

`status` prints the OS, the architecture, the suggested RID, and `Lua:` and `Wasmtime:` lines, with per-OS fixes for a missing one. It exits 0 either way. `titanium scripting-deps install` prints the supported routes (the matching RID archive, or `tools/packaging/bundle-scripting-native.ps1`) and exits 1 when something is missing, because Titanium does not install these through a system package manager. See [CLI](/docs/cli#scripting-deps).

Nothing native loads until a configuration names a script kind. The two natives load independently, so a Lua-only configuration does not need Wasmtime.

### Per-RID matrix

`tools/packaging/scripting-native.lock.json` pins the version, URL, and SHA-256 of every native. Binaries are never committed; the bundle script downloads and verifies them.

| RID | Lua native | Wasmtime native | Wasmtime archive |
| --- | --- | --- | --- |
| `win-x64` | `lua54.dll` | `wasmtime.dll` | `x86_64-windows-c-api.zip` |
| `linux-x64` | `liblua54.so` | `libwasmtime.so` | `x86_64-linux-c-api.tar.xz` |
| `linux-arm64` | `liblua54.so` | `libwasmtime.so` | `aarch64-linux-c-api.tar.xz` |
| `linux-musl-x64` | `liblua54.so` | `libwasmtime.so` | `x86_64-musl-c-api.tar.xz` |
| `linux-musl-arm64` | `liblua54.so` | `libwasmtime.so` | `aarch64-musl-c-api.tar.xz` |
| `osx-x64` | `liblua54.dylib` | `libwasmtime.dylib` | `x86_64-macos-c-api.tar.xz` |
| `osx-arm64` | `liblua54.dylib` | `libwasmtime.dylib` | `aarch64-macos-c-api.tar.xz` |

- **Wasmtime** is downloaded and its SHA-256 checked for every RID. Wasmtime runs with signal-based traps turned off on all of them, so it installs no `SIGSEGV` or `SIGILL` handler that could fight the .NET runtime's own.
- **Lua** is compiled from the pinned lua.org tarball plus the shim sources, on a build machine of the same OS and architecture as the RID. Where that is not possible, for example `linux-musl-*` without an Alpine runner, the bundle script warns and skips Lua (`-Strict` makes it fail). That archive then loads Wasm plugins but reports `Lua: not found`.
- **Alpine and Kubernetes musl images** need `linux-musl-x64` or `linux-musl-arm64`, not the regular Linux archive.
- `win-arm64` has no pinned native, so neither engine loads there.
- **Development.** `tools/scripting/build-lua-shim.ps1` builds the Lua native locally, and `TITANIUM_SCRIPTING_NATIVE_DIR` names a folder searched for it first. See `tools/scripting/README.md`.

## Security model

A script is configuration you wrote, but it runs in the request path of a proxy that faces the network, so the engines assume it can be wrong, slow, or hostile.

| Control | Lua | WebAssembly |
| --- | --- | --- |
| Isolation | One Lua state per pool slot, with `io`, `package`, `require`, `debug`, `load*`, `os.execute`, and bytecode removed. | One Wasmtime store and linear memory per VM. The guest has no files, no environment, and no sockets. |
| Memory | `limits.memoryMb`, 64 by default, enforced by a capped allocator. | `limits.memoryMb`, 64 by default, at most 4096. |
| CPU | `limits.maxInstructions` (10,000,000 per call) and `limits.epochDeadlineMs`, both enforced inside the engine. | `limits.epochDeadlineMs` (Wasmtime epoch interruption) and, when set, `limits.fuel`. |
| Wall time | `timeoutMs` (default 1000, at most 60,000), including waits for callouts. | The same `timeoutMs`. |
| Faults | The state that hit a limit is retired and rebuilt in the background. | A VM that traps leaves its slot and a new one is built off the request path. |
| Network | `http.call` reaches only `calloutHosts`. | `proxy_http_call`, `proxy_grpc_call`, and `proxy_grpc_stream` reach only `calloutHosts`. |
| Reload | The new script is compiled into a full pool and its `on_configure` accepted before the swap. | The module is compiled, every VM instantiated, and `on_vm_start` and `on_configure` accepted before the swap. |

### Sandbox

The engines enforce their limits themselves, so a script that never yields is still stopped. That is different from a .NET plugin, whose timeout is cooperative (see [A plugin that never yields](/docs/plugins#a-plugin-that-never-yields)).

Lua never calls into the proxy. A call takes a snapshot of the request in and returns a list of commands, so a Lua error (a `longjmp`) never crosses a managed frame. A WebAssembly guest sees only the proxy-wasm host functions Titanium implements, plus a small WASI preview1 subset: stdout and stderr go to the plugin's log, clocks and random bytes are real, `proc_exit` traps, and every file call answers `EBADF`.

### `failMode`

`failMode` decides what a failed call means for the request. A call fails when the script errors, hits a limit, times out, or the plugin is unloaded.

| Value | Effect |
| --- | --- |
| `closed` (default) | The request answers `503`. |
| `open` | The request goes on as if the plugin had not run. |

`failMode` applies to the request phases. A failure in `on_log` is logged and ignored either way.

### Callout allow-list

A callout is an outbound HTTP (or, in Wasm, gRPC) request that a script makes to a service. `calloutHosts` is the only thing that permits one:

```yaml
    calloutHosts: [ users.internal, "*.svc.cluster.local", "auth.example.com:8443" ]
```

Each entry is `host` (any port), `host:port`, or `*.domain` (subdomains only). With none set every callout is refused. A callout never uses a system proxy, never follows redirects, never sends cookies, and drops hop-by-hop headers and `Host`. The number in flight is capped by `limits.calloutConcurrency`, and the answer size by `limits.bufferedBodyBytes`.

### Compiled-module cache

Wasmtime compiles a module to machine code, and the cache keeps that code on disk so a restart skips compiling. Whoever can write the cache directory can run code in the proxy, so the cache is used only when the directory passes a permission check: on Unix it must not be writable by group or others (it is created `0700`), and on Windows its ACL must not let Everyone, Users, or Authenticated Users write. A directory that fails is ignored with a warning and modules compile as usual.

The directory is `TITANIUM_WASM_CACHE_DIR`, or `titanium/wasm-cache` under the user's local application data. `TITANIUM_WASM_CACHE_DIR=off` turns the cache off. An entry is keyed by the module's SHA-256 and the engine's fingerprint, so a module compiled under other settings is never reused.

### Native libraries are trusted code

The sandbox protects the proxy from a script. It does not protect the proxy from the native library that runs the script: `lua54` and `wasmtime` execute inside the `titanium` process with its privileges, so a replaced library is a replaced proxy.

- **Where they load from.** The Lua shim is searched for in `TITANIUM_SCRIPTING_NATIVE_DIR` first, then beside the binary, then the default library path. Wasmtime uses the default probing. Treat a writable `TITANIUM_SCRIPTING_NATIVE_DIR`, or a writable application directory, like a writable `titanium` binary.
- **What is checked.** `tools/packaging/scripting-native.lock.json` pins a SHA-256 for every download, and the bundle script verifies it. The proxy does not re-hash a library when it loads it, and the Lua shim is checked only for its exported ABI version. A plain Lua build is refused; a malicious one built to the same ABI is not.
- **What stays off.** With no `kind: lua` or `kind: wasm` plugin in the configuration, no library is loaded or even requested, and the engines are never constructed. Tests assert this on a default reverse configuration.
- **What a hostile guest can reach.** Everything a script hands the host (header maps, property paths, callout targets) is untrusted input. A malformed header map or path is answered with an error code and never throws; the proxy-wasm host-call corpus in `tests/fuzz` replays truncations and mutations of both on every test run.

Review checklist before enabling scripting on a shared host:

1. `failMode` is `closed` (the default) unless a failed script must not take the route down.
2. `calloutHosts` lists exact hosts, not `*`-wide domains you do not control.
3. `limits` and `timeoutMs` are set for the slowest legitimate call, not raised to hide a slow one.
4. The script files, the Wasm modules, the native directory, and `TITANIUM_WASM_CACHE_DIR` are writable only by the account that runs the proxy.

## Lua plugins

```yaml
plugins:
  - name: tag-user
    kind: lua
    file: plugins/tag-user.lua
    failMode: closed        # closed (default): a failed script answers 503. open: the request goes on.
    timeoutMs: 1000         # wall time of one call, including waits
    bodyMode: streaming     # or buffered
    settings:
      greeting: hello       # read as settings.greeting
    limits:
      memoryMb: 64          # per Lua state
      maxInstructions: 10000000
      poolSize: 4           # Lua states; defaults to the processor count, at most 8
      epochDeadlineMs: 800  # CPU time of one call; defaults to 80% of timeoutMs

routes:
  - id: api
    clusterId: api
    match: { path: /api/{**rest} }
    middleware: [tag-user]
```

```lua
-- plugins/tag-user.lua
function on_request_headers(ctx)
  local user = ctx.get_header("x-user")
  if not user then
    return ctx.deny(401, "who are you?")
  end
  ctx.vars.user = user                      -- kept for the later phases of this request
  ctx.set_header("x-greeting", settings.greeting .. " " .. user)
end
```

A Lua plugin named in a route's `middleware:` runs `on_request_headers` (and, with `bodyMode: buffered`, `on_request_body`). Set as the reverse proxy's script pipeline it runs every phase it defines.

### Phase functions

| Function | When |
| --- | --- |
| `on_configure(ctx)` | Once per Lua state when the plugin loads or reloads. An error here fails the load, so the old plugin stays. |
| `on_request_headers(ctx)` | Request headers arrived. |
| `on_request_body(ctx)` | A request body piece (`ctx.body`, `ctx.last`), or the whole body when buffered. |
| `on_request_trailers(ctx)` | Request trailers. |
| `on_response_headers(ctx)` | Response headers arrived (`ctx.status`). |
| `on_response_body(ctx)` | A response body piece, or the whole body when buffered. |
| `on_response_trailers(ctx)` | Response trailers. |
| `on_log(ctx)` | After the response (`ctx.elapsed_ms`, `ctx.error`). Failures here are logged and ignored. |

### `ctx`

`ctx` is a snapshot. Changing a field does nothing; call a method to ask the proxy for a change. Methods work with `ctx.name(...)` and `ctx:name(...)`.

| Field | Meaning |
| --- | --- |
| `phase`, `plugin` | Phase name (`request_headers`, …) and plugin name. |
| `method`, `path`, `host`, `scheme`, `is_https` | The request. |
| `route_id`, `cluster_id` | The matched route and its cluster. |
| `client_ip`, `client_port` | The client. |
| `status` | Response status (response phases). |
| `headers` | Lower-case name to value; repeated headers are joined with `, `. |
| `header_list` | Every header in order as `{name, value}`. |
| `body`, `last` | Body phases: the piece and whether it is the last. |
| `vars` | A table kept across the phases of one request. Strings, numbers, booleans, and tables of them. |
| `settings` | The plugin's `settings`. |

| Method | Effect |
| --- | --- |
| `get_header(name)` | Current value, including edits made earlier in this call. |
| `set_header(name, value)` | Replace or add. Names must be HTTP tokens; values must not contain CR, LF, or NUL. |
| `remove_header(name)` | Remove. |
| `set_body(bytes)` | Replace the body piece (streaming) or the whole body (buffered). |
| `deny(status, body?, headers?)` / `respond(...)` | Answer now with a local response (status 100 to 599, default 403). |
| `choose_cluster(id)` | Request headers, script pipeline only: send the request to another cluster. |

### Lua sandbox

A script sees `string`, `table`, `math`, `utf8`, `coroutine`, the base functions, and `os.time`, `os.clock`, `os.date`, `os.difftime`. It has no `io`, `package`, `require`, `debug`, `load`, `loadfile`, `dofile`, `string.dump`, or `os.execute`/`getenv`/`exit`/`remove`/`rename`. Files must be Lua source; precompiled bytecode is refused. `collectgarbage` accepts only `collect`, `count`, `step`, and `isrunning`.

A wait (`http.call`, `time.sleep`, `jwt.verify`) suspends the call's coroutine; the proxy does the work and resumes the call with the result. The state serves other calls while one waits. `on_configure` cannot wait.

### Libraries

| Library | Functions |
| --- | --- |
| `json` | `encode(value)`, `decode(text)` (raises on bad JSON), `json.null`. At most 64 levels deep. |
| `base64` | `encode`, `encode_url` (no padding), `decode`, `decode_url` (either alphabet, padding optional; `nil, err` on bad input). |
| `hex` | `encode` (lower case), `decode` (`nil, err` on bad input). |
| `url` | `encode` (RFC 3986), `decode(s, plus)` (`plus` turns `+` into a space), `parse_query(s)`, `build_query(t)`. |
| `crypto` | `md5`, `sha1`, `sha256`, `sha512` (raw bytes; wrap in `hex.encode`), `hmac(alg, key, msg)`, `equals(a, b)` (constant time). |
| `regex` | `match(p, s, flags)` (the groups, or the whole match), `find(p, s, flags)` (start, end, groups), `test(p, s, flags)`. Flag `i` ignores case. Linear-time matcher: no backtracking blow-ups. Pattern at most 1024 bytes, input at most 1 MiB. |
| `uuid` | `v4()`. |
| `random` | `bytes(n)`, `int(m, n)` from the OS random source. |
| `time` | `now()` (seconds, with fraction), `now_ms()`, `monotonic_ms()`, `sleep(ms)` (waits). |
| `cidr` | `match(ip, "10.0.0.0/8" or { ... })`, `valid(ip)`. IPv4, IPv6, and IPv4-mapped IPv6. |
| `log` | `debug`, `info`, `warn`, `error` to the proxy log. `print` is `log.info`. |
| `metrics` | `counter(name[, value, labels])`, `gauge(name, value, labels)`, `histogram(name, value, labels)`. |
| `timer` | `at(ms, "function", arg)`, `every(ms, "function", arg)` (at least 50 ms). |
| `http` | `call(url[, opts])` or `call(opts)`. |
| `jwt` | `verify(token[, { iss = ..., aud = ... }])`. |
| `shared` | `dict(name)`. |

### Lua callouts

```yaml
  - name: enrich
    kind: lua
    file: plugins/enrich.lua
    timeoutMs: 2000
    calloutHosts: [ users.internal, "*.svc.cluster.local", "auth.example.com:8443" ]
    limits:
      calloutConcurrency: 16      # callouts in flight for this plugin; extra calls get nil, err at once
      bufferedBodyBytes: 1048576  # largest callout answer read
```

```lua
local r, err = http.call("http://users.internal/v1/" .. url.encode(user), {
  method = "GET", timeout_ms = 300, headers = { accept = "application/json" } })
if not r then return ctx.deny(503, err) end
local profile = json.decode(r.body)            -- r.status, r.headers (lower-case names), r.body
```

The allow-list rules are in the [security model](#callout-allow-list). Failures, refusals, and `timeout_ms` (default 5000, at most 60000) come back as `nil, err`. `timeoutMs` still bounds the whole call: past it the proxy abandons the call and cancels the callout.

### Shared dictionaries

```yaml
    settings:
      sharedDicts: "rate:100000,cache:5000"   # name:max items (default 10000)
    limits:
      sharedDataBytes: 16777216                 # bytes per dictionary
```

```lua
local rate = shared.dict("rate")
local n = rate:incr(ctx.client_ip, 1, 0, 60)   -- create at 0 with a 60 s TTL, then add 1
if n > 100 then return ctx.deny(429) end
```

A dictionary is shared by every state of every plugin in the process, and survives reload. Values are strings, numbers, and booleans. Methods: `get(k)`, `set(k, v[, ttl])` (a nil value deletes), `add(k, v[, ttl])` (`nil, "exists"` when present; `set` and `add` return `true` or `nil, err`), `incr(k, delta[, init[, init_ttl]])`, `delete(k)`, `ttl(k)` (seconds left, `0` for no expiry, `nil` when missing), `count()`. TTLs are seconds and may have a fraction. When a dictionary is full, expired entries go first, then the least recently used.

### JWT

`jwt.verify` checks a token against the JSON Web Key Set in `settings.jwks` and returns its claims, or `nil, err`.

```yaml
    settings:
      jwks: '{"keys":[{"kty":"RSA","kid":"k1","n":"...","e":"AQAB"}]}'
```

RS256/384/512, PS256/384/512, ES256/384/512, and HS256/384/512 (with an `oct` key) are supported; `none` is always refused. A token's `kid` and `alg` must match the key's when the key sets them, and an RSA or EC key is never used as an HMAC secret. `exp` and `nbf` are checked with 60 seconds of skew; `iss` and `aud` are checked when passed.

### Timers

`timer.at(ms, "fn", arg)` calls the global `fn(ctx)` once after `ms`; `timer.every` calls it repeatedly. `ctx.phase` is `timer` and `ctx.arg` is `arg`. Timers started in `on_configure` are started once per plugin, not once per state. A plugin has at most 256 timers. Timers stop when the plugin is reloaded or the proxy stops; the new plugin starts its own from `on_configure`.

### Lua metrics

`metrics.counter("cache_hits", 1, { route = ctx.route_id })` publishes `twp.scripting.lua.user.cache_hits` on the `Titanium.Web.Proxy.Scripting` meter, tagged with `plugin` and the labels (at most 8). Names match `[A-Za-z_][A-Za-z0-9_]*`. A name keeps the kind it was first used with, and the process keeps at most 512 script instruments; later ones are dropped with a warning. OTLP metrics export (`server.telemetry`) includes this meter.

The engine also publishes `twp.scripting.lua.call.duration`, `twp.scripting.lua.errors`, and `twp.scripting.lua.states.retired`.

### L4 phases

A Lua plugin is also an `IStreamScriptFilter`. Name it in `listeners[].streamFilters` on a passthrough (transparent or QUIC) listener, or set `StreamFilters` from a .NET host, to see raw TCP connections.

| Function | When |
| --- | --- |
| `on_connect(ctx)` | A connection was accepted (`ctx.client_ip`, `ctx.server_name` from SNI). `ctx.deny()` closes it; `ctx.choose_cluster(id)` picks the upstream. |
| `on_data(ctx)` | Bytes arrived. `ctx.direction` is `downstream` (from the client) or `upstream`; `ctx.data` is the piece. `ctx.set_data(bytes)` replaces it (empty drops it); `ctx.deny()` closes the connection. |
| `on_close(ctx)` | The connection closed. |

`ctx.vars` lasts for the connection.

### Several scripts on one pipeline

In a .NET host, `new ScriptPhaseHostChain(hosts)` runs several script hosts (Lua or Wasm) as one `ReverseProxyOptions.ScriptPipeline`, in list order. Each host sees the header and body edits of the hosts before it; the first deny wins and the rest are skipped. The last cluster choice wins. `on_log` runs on every host, and one host's failure does not stop the next.

### Lua limits

Every limit is enforced inside the engine, so it holds even when the script catches errors with `pcall`.

| Limit | Default | When hit |
| --- | --- | --- |
| `limits.maxInstructions` | 10,000,000 per call | The call fails with an instruction-limit error. |
| `limits.epochDeadlineMs` | 80% of `timeoutMs` | CPU time spent in Lua per call; the call fails with a time-limit error. |
| `limits.memoryMb` | 64 per state | Allocations past the cap are refused; the call fails with out-of-memory. |
| `timeoutMs` | 1000 | Wall time including waits. The proxy stops waiting; this is the backstop. |
| `string.rep`, `string.format` | 16 MiB result | The call fails. |
| Lua patterns | 4096-byte pattern, 5,000,000 matching steps | The call fails. |
| `regex` | 1024-byte pattern, 1 MiB input | The call fails. |
| `limits.calloutConcurrency` | 16 | Extra callouts return `nil, err` at once. |
| `limits.bufferedBodyBytes` | 1 MiB | A larger callout answer returns `nil, err`. |
| `limits.sharedDataBytes` | 16 MiB per dictionary | Least recently used entries are evicted; a value that cannot fit returns `nil, err`. |

A state that hit the instruction, time, or memory limit is retired: it takes no new calls and is rebuilt in the background, never on a request thread. A failed call answers `503` with `failMode: closed` and lets the request through with `failMode: open`.

Reload compiles the new script into a full pool of states and runs `on_configure` before the swap. A script that does not compile, or whose `on_configure` fails, never replaces the running one.

## WebAssembly plugins

`kind: wasm` runs [proxy-wasm](https://github.com/proxy-wasm/spec) modules on Wasmtime. Modules written with the Rust, Go, or C++ proxy-wasm SDKs run unchanged, and the engine speaks ABI 0.1.0, 0.2.0, and 0.2.1.

```yaml
plugins:
  - name: add-header
    kind: wasm
    file: plugins/add_header.wasm     # a .wat file is compiled from text
    failMode: closed
    timeoutMs: 1000
    bodyMode: streaming               # or buffered
    vmId: shared-vm                   # optional: plugins with the same vmId and module share VMs
    calloutHosts: [ auth.internal ]
    settings:
      configuration: '{"header":"x-from-wasm"}'   # given to the guest in on_configure
      vmConfiguration: ''                         # given to the guest in on_vm_start
    limits:
      memoryMb: 64
      fuel: 50000000                  # optional instruction budget per call
      epochDeadlineMs: 800
      poolSize: 4                     # VMs; defaults to the processor count, at most 1024
      calloutConcurrency: 64
      bufferedBodyBytes: 4194304
      sharedDataBytes: 16777216
      queueBytes: 4194304

routes:
  - id: api
    clusterId: api
    match: { path: /api/{**rest} }
    middleware: [add-header]
```

A Wasm plugin is fully built before it replaces anything: the module is compiled, every VM instantiated, and `on_vm_start` and `on_configure` accepted. A reload that fails keeps the old plugin.

### Wasm phases and contexts

As a route plugin it runs the request-headers phase. Set as the reverse proxy's script pipeline in a .NET host, it runs every HTTP phase: request headers, body, and trailers, then the response ones, then the log phase. On a passthrough listener's `StreamFilters` it runs as a proxy-wasm network filter, with `on_new_connection`, `on_downstream_data`, `on_upstream_data`, and the close callbacks. Each request or connection gets its own context in one VM for its whole life.

`settings.configuration` is the plugin configuration the guest reads in `on_configure`. With no such key and other settings present, the settings go to the guest as a JSON object. `settings.vmConfiguration` is the VM configuration for `on_vm_start`.

### Host functions

| Area | What a guest can do |
| --- | --- |
| Headers, body, trailers | Read and edit header maps, read and replace body buffers, send a local response, pause and resume a request. |
| Properties | `proxy_get_property` with the attribute names Envoy uses (request, response, connection, upstream, plugin). Attributes Titanium has no value for answer empty, not an error. |
| Callouts | `proxy_http_call`, `proxy_grpc_call`, and `proxy_grpc_stream` with `proxy_grpc_send`, `proxy_grpc_close`, and `proxy_grpc_cancel`, limited to `calloutHosts`. A request paused on a callout resumes when the guest continues it. |
| Shared data and queues | `proxy_get_shared_data`, `proxy_set_shared_data` with CAS, and shared queues with `proxy_on_queue_ready`, scoped by `vmId`. |
| Timers | `proxy_set_tick_period_milliseconds` and `proxy_on_tick` from a root context. |
| Metrics | `proxy_define_metric`, `proxy_increment_metric`, `proxy_record_metric`, `proxy_get_metric`. |
| Foreign functions | `proxy_call_foreign_function` with `compress` and `uncompress` (zlib, as in Envoy); the uncompressed output is capped. |
| Logging and time | `proxy_log`, `proxy_get_current_time_nanoseconds`, and the WASI subset described in the [security model](#sandbox). |

### Choosing the upstream

A guest picks another cluster by setting the `twp.upstream_cluster` property with `proxy_set_property` in the request-headers phase, or on a stream filter when the connection opens. It takes effect in a .NET host that sets the script pipeline or the stream filters, as `choose_cluster` does in Lua.

### Sharing a VM with `vmId`

Plugins with the same `vmId`, module bytes, engine, and limits run in the same VMs, each with its own root context, as Envoy shares a VM between filters. Shared data and queues are scoped by `vmId`, and a plugin with no `vmId` uses the empty scope. A pool and its shared state live while a plugin uses them.

### WebAssembly limits

| Limit | Default | When hit |
| --- | --- | --- |
| `limits.memoryMb` | 64 per VM (1 to 4096) | Growing linear memory past the cap fails inside the guest. |
| `limits.epochDeadlineMs` | `timeoutMs`, up to 60,000 | The call traps. Wasmtime checks every 2 ms. |
| `limits.fuel` | off | The call traps when the instruction budget runs out. Setting it uses a second Wasmtime engine, because fuel is an engine setting that costs every call. |
| `timeoutMs` | 1000 | Wall time of a request phase, including a pause on a callout. |
| `limits.poolSize` | processor count (1 to 1024) | Requests are spread round robin over healthy VMs. |
| `limits.calloutConcurrency` | 64 (0 to 10,000) | Further callouts fail inside the guest. |
| `limits.bufferedBodyBytes` | 4 MiB | A body or callout answer past it is refused. |
| `limits.sharedDataBytes` | 16 MiB per `vmId` scope | A write past it fails with an internal failure. |
| `limits.queueBytes` | 4 MiB per queue | An enqueue past it fails. |
| Guest metrics | 10,000 per process | A new metric fails to define. |

A VM that traps is discarded and rebuilt in the background with every attached plugin's root context started again. A failed call answers `503` with `failMode: closed` and lets the request through with `failMode: open`.

### WebAssembly metrics

The engine publishes `twp.scripting.wasm.call.duration` (tagged `plugin` and `phase`) and `twp.scripting.wasm.errors` (tagged `plugin` and `reason`) on the `Titanium.Web.Proxy.Scripting` meter. Metrics a guest defines appear as three fixed instruments, `twp.scripting.wasm.guest.counter`, `.gauge`, and `.histogram`, tagged with the metric name and `vm_id`, so a guest cannot create instruments by choosing names.

## Parity checklist

Other gateways script their request path in different ways. This table lists what each one offers, as documented at the time of writing, and what Titanium has in its place. It compares kinds of feature, not APIs: a script written for OpenResty or Kong does not run here unchanged.

| Gateway | Scripting it offers | In Titanium |
| --- | --- | --- |
| Envoy | A Lua HTTP filter, and proxy-wasm filters for HTTP and TCP. | `kind: lua`, and `kind: wasm` with proxy-wasm 0.1.0 to 0.2.1. Wasm modules built for Envoy's HTTP and network filters run here. |
| NGINX | njs (JavaScript); Lua through a third-party module. | Lua 5.4 and Wasm. There is no JavaScript engine. |
| OpenResty | LuaJIT with the `ngx.*` API, cosockets, and `lua_shared_dict`. | Lua 5.4 with `ctx`, the libraries above, `shared.dict`, and `http.call`. No `ngx.*`, no LuaJIT, no cosockets. |
| Kong | Lua plugins on the PDK, and proxy-wasm filters. | Lua plugins named in `middleware:`, and proxy-wasm modules. No PDK. |
| HAProxy | Lua actions, fetches, and services; SPOE for external agents. | Lua request and response phases. SPOE is covered by the `extAuth` policy or a callout. HAProxy has no proxy-wasm. |
| Traefik | Go plugins run by Yaegi, and WebAssembly middleware through http-wasm. | Lua and proxy-wasm. The http-wasm ABI is not supported, and Go plugins are replaced by [.NET plugins](/docs/plugins). |
| APISIX | Lua plugins, external plugin runners, and a proxy-wasm plugin. | Lua and proxy-wasm in-process. There is no external plugin runner; use `extAuth` or a callout. |

Checklist of what the engines cover, with the part of the surface that is not yet reachable from `twp.yaml`:

| Capability | Lua | Wasm | Reachable from `twp.yaml` |
| --- | --- | --- | --- |
| Read and edit request and response headers | Yes | Yes | Request headers (route `middleware:`) |
| Read and replace bodies, streaming or buffered | Yes | Yes | Request body with `bodyMode` |
| Answer locally (deny or respond) | Yes | Yes | Yes |
| Choose the upstream cluster | Yes | Yes | No (.NET host, script pipeline) |
| Outbound HTTP, allow-listed | Yes (`http.call`) | Yes (`proxy_http_call`) | Yes (`calloutHosts`) |
| Outbound gRPC | No | Yes | Yes (`calloutHosts`) |
| State shared across requests and states | Yes (`shared.dict`) | Yes (shared data and queues) | Yes |
| JWT verification | Yes (`jwt.verify`) | Guest code | Yes |
| Timers | Yes (`timer`) | Yes (ticks) | Yes |
| User metrics on the OTLP and Prometheus meter | Yes | Yes | Yes |
| Raw TCP (L4) filters | Yes | Yes | No (.NET host, `StreamFilters`) |
| Several scripts on one pipeline | Yes (`ScriptPhaseHostChain`) | Yes | No (.NET host) |
| Per-script memory, CPU, and wall-time limits inside the engine | Yes | Yes | Yes |
| A failed reload keeps the running script | Yes | Yes | Yes |

What Titanium does not offer:

- **OpenResty, Kong PDK, and APISIX Lua APIs.** There is no `ngx.*`, no Kong PDK, no APISIX plugin API, and no LuaJIT or FFI.
- **Cosockets and arbitrary sockets.** A script reaches the network only through `http.call` or a Wasm callout to an allow-listed host.
- **`require` and Lua modules.** A Lua plugin is one file.
- **The http-wasm ABI** that Traefik uses. Titanium runs proxy-wasm modules only.

If a case is not covered, open an issue describing it.