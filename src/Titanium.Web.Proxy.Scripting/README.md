# Titanium.Web.Proxy.Scripting

Embedded scripting engines for Titanium Web Proxy plugins (`plugins[].kind`).

- `kind: wasm` runs [proxy-wasm](https://github.com/proxy-wasm/spec) modules (ABI 0.1.0, 0.2.0, 0.2.1) on the
  Wasmtime C API through hand-written bindings. The `wasmtime` native library must sit next to the application
  (see `tools/scripting/README.md`).

Call `ScriptingBootstrap.Register()` once at startup. Engines register lazily, so nothing native loads until a
configuration names a script kind.
