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

Lua never calls into the proxy. A call takes a snapshot in and returns a list of commands; waits (in a later release: callouts and timers) suspend the call's coroutine and the proxy resumes it.

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

A state that hit the instruction, time, or memory limit is retired: it takes no new calls and is rebuilt in the background, never on a request thread. A failed call answers `503` with `failMode: closed` and lets the request through with `failMode: open`.

Reload compiles the new script into a full pool of states and runs `on_configure` before the swap. A script that does not compile, or whose `on_configure` fails, never replaces the running one.

### Native library

The engine needs `lua54.dll` / `liblua54.so` / `liblua54.dylib` beside the binary: Lua 5.4.7 built together with the Titanium shim. Release archives include it. `titanium scripting-deps status` reports whether it loads. Without it, a configuration that names a Lua plugin fails to load with a message that says so; nothing else changes. To build it yourself see `tools/scripting/README.md`.

## WebAssembly plugins

`kind: wasm` runs [proxy-wasm](https://github.com/proxy-wasm/spec) modules on Wasmtime.
