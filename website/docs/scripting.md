# Scripting

Write a route filter in Lua 5.4 or ship one as a WebAssembly module, and run it behind the same `middleware:` list as a .NET plugin. This page is for operators and plugin authors who want to edit or add a filter without building a .NET assembly.

The decision, the cost budgets, and the safety rules are on [Scripting engines](/docs/plugins#scripting-engines) in Per-route plugins.

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

### Sandbox

A script sees `string`, `table`, `math`, `utf8`, `coroutine`, the base functions, and `os.time`, `os.clock`, `os.date`, `os.difftime`. It has no `io`, `package`, `require`, `debug`, `load`, `loadfile`, `dofile`, `string.dump`, or `os.execute`/`getenv`/`exit`/`remove`/`rename`. Files must be Lua source; precompiled bytecode is refused. `collectgarbage` accepts only `collect`, `count`, `step`, and `isrunning`.

Lua never calls into the proxy. A call takes a snapshot in and returns a list of commands. A wait (`http.call`, `time.sleep`, `jwt.verify`) suspends the call's coroutine; the proxy does the work and resumes the call with the result. The state serves other calls while one waits. `on_configure` cannot wait.

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

### Callouts

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

`calloutHosts` is the allow-list: `host` (any port), `host:port`, or `*.domain` (subdomains only). With none set every callout is refused. A callout never uses a system proxy, never follows redirects, never sends cookies, and drops hop-by-hop headers and `Host`. Failures, refusals, and `timeout_ms` (default 5000, at most 60000) come back as `nil, err`. `timeoutMs` still bounds the whole call: past it the proxy abandons the call and cancels the callout.

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

### Metrics

`metrics.counter("cache_hits", 1, { route = ctx.route_id })` publishes `twp.scripting.lua.user.cache_hits` on the `Titanium.Web.Proxy.Scripting` meter, tagged with `plugin` and the labels (at most 8). Names match `[A-Za-z_][A-Za-z0-9_]*`. A name keeps the kind it was first used with, and the process keeps at most 512 script instruments; later ones are dropped with a warning. OTLP metrics export (`server.telemetry`) includes this meter.

The engine also publishes `twp.scripting.lua.call.duration`, `twp.scripting.lua.errors`, and `twp.scripting.lua.states.retired`.

### L4 phases

A Lua plugin is also an `IStreamScriptFilter`. In a .NET host, add it to a transparent endpoint's `StreamFilters` to see raw TCP connections:

| Function | When |
| --- | --- |
| `on_connect(ctx)` | A connection was accepted (`ctx.client_ip`, `ctx.server_name` from SNI). `ctx.deny()` closes it; `ctx.choose_cluster(id)` picks the upstream. |
| `on_data(ctx)` | Bytes arrived. `ctx.direction` is `downstream` (from the client) or `upstream`; `ctx.data` is the piece. `ctx.set_data(bytes)` replaces it (empty drops it); `ctx.deny()` closes the connection. |
| `on_close(ctx)` | The connection closed. |

`ctx.vars` lasts for the connection.

### Several scripts on one pipeline

In a .NET host, `new ScriptPhaseHostChain(hosts)` runs several script hosts (Lua or Wasm) as one `ReverseProxyOptions.ScriptPipeline`, in list order. Each host sees the header and body edits of the hosts before it; the first deny wins and the rest are skipped. The last cluster choice wins. `on_log` runs on every host, and one host's failure does not stop the next.

### Limits

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

### Native library

The engine needs `lua54.dll` / `liblua54.so` / `liblua54.dylib` beside the binary: Lua 5.4.7 built together with the Titanium shim. Release archives include it. `titanium scripting-deps status` reports whether it loads. Without it, a configuration that names a Lua plugin fails to load with a message that says so; nothing else changes. To build it yourself see `tools/scripting/README.md`.

## WebAssembly plugins

`kind: wasm` runs [proxy-wasm](https://github.com/proxy-wasm/spec) modules on Wasmtime.
