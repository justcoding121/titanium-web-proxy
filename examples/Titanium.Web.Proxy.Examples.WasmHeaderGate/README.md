# WASM header gate

The same filter as `../Titanium.Web.Proxy.Examples.RoutePlugin` and `../Titanium.Web.Proxy.Examples.LuaHeaderGate`, written as a [proxy-wasm](https://github.com/proxy-wasm/spec) module: a request without one header gets `403`, and a request with it goes on with `x-route-plugin: header-gate` added.

Three sources build the same module:

| File | Language | Needs | Required header |
| --- | --- | --- | --- |
| `header_gate.wat` | WebAssembly text | nothing; the engine compiles `.wat` itself | fixed `x-api-client` |
| `src/lib.rs` + `Cargo.toml` | Rust, `proxy-wasm` crate | `rustup`, the `wasm32-wasip1` target | `settings.configuration`, default `x-api-client` |
| `tinygo/main.go` | Go, `proxy-wasm-go-sdk` | TinyGo | `settings.configuration`, default `x-api-client` |

`header_gate.wat` is checked by the scripting tests (`ExampleScriptsTests`) on every run. The Rust and TinyGo sources were written against the SDK APIs of the versions below and were not compiled where this sample was written, so build them once before relying on them. No `.wasm` is committed: a binary in the repository cannot be reviewed, and every source here builds in one command.

## Try it with no toolchain

```yaml
plugins:
  - name: header-gate
    kind: wasm
    file: ./header_gate.wat
```

The whole file is [`twp.fragment.yaml`](twp.fragment.yaml). Check that the Wasmtime native loads (`titanium scripting-deps status`), run `titanium run -c twp.yaml` from the directory of the config, then:

```powershell
curl.exe -i http://127.0.0.1:8080/api                                  # 403, missing x-api-client
curl.exe -i -H "X-Api-Client: demo" http://127.0.0.1:8080/api          # the backend sees x-route-plugin: header-gate
```

## Build the Rust version

```powershell
rustup target add wasm32-wasip1
cargo build --release --target wasm32-wasip1
# target/wasm32-wasip1/release/header_gate.wasm
```

Then point `file:` at that path. `proxy-wasm = "0.2"` and `log = "0.4"` are the only dependencies. The crate uses the `proxy_wasm::main!` macro of recent 0.2.x releases; on an older one, replace it with a `#[no_mangle] pub fn _start()` that makes the same two calls. The Titanium engine provides the proxy-wasm ABI and WASI imports a Rust module needs.

The Rust version reads its plugin configuration in `on_configure`. In `twp.yaml` that is `settings.configuration`; a configuration the guest rejects (here: bytes that are not UTF-8) fails the load, so a reload keeps the plugin it had.

## Build the TinyGo version

```powershell
cd tinygo
go mod tidy          # adds github.com/tetratelabs/proxy-wasm-go-sdk and go.sum
tinygo build -o header_gate.wasm -scheduler=none -target=wasi main.go
```

Then point `file:` at `./tinygo/header_gate.wasm`. `-scheduler=none` is what the proxy-wasm Go SDK documents for TinyGo builds. Use the `proxy-wasm-go-sdk` release that matches your TinyGo (`go.mod` lists the requirement after `go mod tidy`).

## Wiring

A `kind: wasm` plugin in a route's `middleware:` runs the request-headers phase for that route. In the top-level `scriptPipeline:` it runs every HTTP phase on every request, and on a passthrough listener's `streamFilters:` it runs as a network filter (`on_new_connection`, `on_downstream_data`, `on_upstream_data`). A module that only defines `on_http_request_headers`, like this one, does nothing in the other phases.

`limits.fuel` caps the instructions of one call, and `limits.epochDeadlineMs` its CPU time. `failMode: closed` answers `503` when the module traps or times out. The scripting guide is `website/docs/scripting.md`.
