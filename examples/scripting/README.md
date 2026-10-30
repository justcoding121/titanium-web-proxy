# Lua scripting recipes

Small Lua scripts for the `titanium` CLI. Each one is a complete plugin; [`twp.scripting.yaml`](twp.scripting.yaml) wires all of them. They run in a sandbox with per-call memory, instruction, and time limits, and the scripting tests (`tests/Titanium.Web.Proxy.Scripting.Tests/ExampleScriptsTests.cs`) run every one through the real engine.

| Script | Phase | What it does | Needs |
| --- | --- | --- | --- |
| [`auth_callout.lua`](auth_callout.lua) | `on_request_headers` | Sends the `Authorization` header to an auth service with `http.call`. 2xx passes; 401 and 403 are passed on; a timeout, a refused callout, or a 5xx denies with 503. | `calloutHosts`, `settings.authUrl` |
| [`jwt_check.lua`](jwt_check.lua) | `on_request_headers` | `jwt.verify` of a bearer token against `settings.jwks`, with `iss` and `aud` checks. Sets `x-jwt-sub` and `x-jwt-scope`. | `settings.jwks` |
| [`body_rewrite.lua`](body_rewrite.lua) | `on_response_body` | Replaces text in text and JSON response bodies. | `bodyMode: buffered`, `settings.from`, `settings.to` |
| [`rate_limit.lua`](rate_limit.lua) | `on_request_headers` | Fixed-window counter per client IP in a shared dictionary; 429 with `retry-after`. | `settings.sharedDicts` |
| [`request_logger.lua`](request_logger.lua) | `on_log` | One JSON line per finished request, plus a latency histogram. | `scriptPipeline` |
| [`tcp_filter.lua`](tcp_filter.lua) | `on_connect`, `on_data`, `on_close` | Refuses clients by CIDR and closes a connection past a byte cap. | a passthrough listener with `streamFilters` |

## Wiring

A plugin is declared once under `plugins:`. Then it runs in one or more of three places:

```yaml
scriptPipeline: [rate-limit, jwt-check, request-logger]   # every request of every route, in this order

routes:
  - id: admin
    match: { path: /admin, pathKind: Prefix }
    clusterId: backend
    middleware: [auth-callout]                            # request headers of this route only

listeners:
  - port: 8443
    passthrough: true
    sniDefaultClusterId: tcp-backend
    streamFilters: [tcp-filter]                           # each raw TCP connection of this listener
```

- **`scriptPipeline`** runs every HTTP phase a script defines (`on_request_headers`, `on_request_body`, `on_response_headers`, `on_response_body`, trailers, `on_log`). Each script sees the header and body edits of the ones before it, the first deny wins, and the pipeline replaces terminate-lite for the whole proxy. `on_log` only runs here.
- **`middleware`** on a route runs the plugin's request-headers phase (and its request-body phase when `bodyMode: buffered`) for that route only.
- **`streamFilters`** on a passthrough listener runs the L4 functions. The list is read at start-up; the scripts themselves reload with the configuration.

List a plugin in `scriptPipeline` or in `middleware`, not both, or its request-headers phase runs twice (`titanium test` warns about it).

## Try it

```powershell
titanium scripting-deps status                 # the Lua and Wasmtime natives must load
cd examples/scripting
titanium test -c twp.scripting.yaml --strict
titanium run -c twp.scripting.yaml             # relative script paths resolve from the config's directory
```

`twp.scripting.yaml` expects a backend on `127.0.0.1:9000`, an auth service at `auth.internal`, and a JWT issuer; edit the `settings` to point at yours.

## Writing your own

- A script defines global functions named after the phases. `ctx` is a snapshot; ask for changes with `ctx.set_header`, `ctx.remove_header`, `ctx.set_body`, `ctx.deny`, `ctx.choose_cluster`.
- Header values a client controls are untrusted: remove the headers your script sets (`x-auth-user`, `x-jwt-sub`) before setting them, as the recipes do.
- A wait (`http.call`, `time.sleep`) suspends only that call. Callouts are refused unless `calloutHosts` names the host.
- `failMode: closed` (default) answers 503 when a script fails or times out. Use `open` only for scripts that decorate and never protect.

The full API is in `website/docs/scripting.md`; `../Titanium.Web.Proxy.Examples.LuaHeaderGate` and `../Titanium.Web.Proxy.Examples.WasmHeaderGate` are the smallest complete examples.
