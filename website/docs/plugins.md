# Per-route .NET plugins

Run your own .NET code for the routes you choose, and for no other route. A plugin is a class that implements `IProxyMiddleware` from `Titanium.Web.Proxy.Abstractions`. The CLI loads it from a DLL, runs it behind a timeout and an exception wall, and unloads it when the configuration reloads.

**Next:** build the sample in `examples/Titanium.Web.Proxy.Examples.RoutePlugin` → list it under `plugins:` → name it in a route's `middleware:`.

## Configure

```yaml twp-fragment
plugins:
  - name: header-gate                 # 1-64 characters of A-Z a-z 0-9 . _ -, unique
    assembly: ./plugins/Titanium.Web.Proxy.Examples.RoutePlugin.dll
    typeName: Titanium.Web.Proxy.Examples.RoutePlugin.HeaderGatePlugin   # optional
    settings:                         # strings, passed to the constructor
      requiredHeader: X-Api-Client
    failMode: closed                  # closed (default) or open
    timeoutMs: 200                    # default 1000, 1 to 60000

routes:
  - id: api
    clusterId: backend
    match: { path: /api, pathKind: Prefix }
    middleware: [header-gate]         # runs in this order, for this route only
```

| Field | Meaning |
|-------|---------|
| `name` | Referenced by `routes[].middleware` and used as the `plugin` tag on the metrics. |
| `assembly` | Path to the plugin DLL. A relative path is resolved against the working directory of the process. |
| `typeName` | Full name of the plugin class. Leave it out when the assembly holds exactly one public class that implements `IProxyMiddleware`. |
| `settings` | A string map handed to the constructor. |
| `failMode` | What a thrown exception or a timeout does. `closed` answers `503`. `open` lets the request continue as if the plugin had called `next`. |
| `timeoutMs` | Longest the plugin may run for one request. |

`titanium test` (and every load and reload) checks that the assembly file exists and loads, that it holds a class that implements the contract at a supported version, and that every `middleware` name refers to a plugin. A plugin that no route names is reported as a warning.

## Write a plugin

Reference `Titanium.Web.Proxy.Abstractions` and `Titanium.Web.Proxy` with `Private=false` and `ExcludeAssets=runtime`, so your build does not copy the host's own assemblies next to the plugin. The host always supplies those two assemblies (and the logging abstractions) to every plugin, so both sides agree on each type that crosses the boundary.

```csharp
[ProxyPlugin(ProxyPluginContract.CurrentVersion)]
public sealed class HeaderGatePlugin : IProxyMiddleware
{
    public HeaderGatePlugin(IReadOnlyDictionary<string, string> settings) { /* ... */ }

    public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken ct)
    {
        if (context.Session is not SessionEventArgs session) return next(context, ct);

        if (!session.HttpClient.Request.Headers.HeaderExists("X-Api-Client"))
        {
            context.HandledStatusCode = 403;
            context.HandledBody = "missing X-Api-Client";
            context.IsHandled = true;      // answer here, do not go on
            return ValueTask.CompletedTask;
        }

        return next(context, ct);          // go on to the next plugin, then the origin
    }
}
```

- The constructor takes `IReadOnlyDictionary<string,string>` (the `settings`) or nothing.
- `ProxyMiddlewareContext.Session` is the request's `SessionEventArgs`. Edit request headers through it. To answer from the plugin, set `IsHandled`, `HandledStatusCode`, and a body or `HandledHeaders`.
- Call `next` to continue. A plugin that neither calls `next` nor sets `HandledStatusCode` stops the request with a `403`.
- Honour the `CancellationToken`: it is cancelled when `timeoutMs` passes.
- A plugin that implements `IDisposable` or `IAsyncDisposable` is disposed before its load context is unloaded.
- Plugins run on every request of the route, possibly at once, so keep instance state thread-safe. Nothing carries over from one request to the next unless the plugin stores it.
- Plugin code runs in the proxy process with the proxy's rights. It is not a sandbox. Load only assemblies you trust.

The full sample, with a README, is in `examples/Titanium.Web.Proxy.Examples.RoutePlugin`.

## What the host guarantees

| Guarantee | Detail |
|-----------|--------|
| Zero cost elsewhere | A route without `middleware` runs no plugin code and adds no per-request work beyond one null check. A plugin that no route names is loaded but never called. |
| Isolation | A plugin gets a private copy of the request context. If it throws or overruns `timeoutMs`, the host drops that copy and applies `failMode`, so a late write from an abandoned call cannot change the request. |
| Timeout | Measures the plugin's own work. The host calls the real `next` after the plugin returns, so a slow origin never counts against `timeoutMs`. It stops a plugin that awaits; it cannot stop one that never yields (see [A plugin that never yields](#a-plugin-that-never-yields)). |
| Reload | A config reload loads the new plugin set first. If any plugin fails to load, the old set stays in service and the reload is rejected. After the swap, the old set is retired: calls already running finish, then each plugin is disposed and its load context unloaded so the runtime can reclaim the assembly. |
| Fresh files | The DLL is read into memory, so a replaced file is picked up by the next reload. |
| Keep-alive | A keep-alive client connection does not carry plugin state. Each request gets its own context. |
| Terminate-lite | A config in which a route names a plugin takes the full session path, like `extAuth`. A config in which no route names a plugin keeps terminate-lite, even when plugins are loaded. |

## A plugin that never yields

The timeout is cooperative. A .NET plugin runs on the request's own thread, and the host starts timing it only when `InvokeAsync` hands back a task. A plugin that spins in a loop, waits on a lock, or makes a blocking call before its first real `await` never hands that task back. It holds the request thread until it finishes, `timeoutMs` never fires, and `failMode` (including `open`) does not apply.

- Write plugins so the first thing that can take long is an `await` that honours the `CancellationToken`.
- Keep CPU-bound work short, or move it behind `await Task.Run(..., ct)` inside the plugin.
- Treat a plugin that cannot do this as an operator risk: plugins are trusted, in-process code, and a stuck one stalls the requests it holds until it returns.

The host does not move plugin calls to the thread pool to work around this. The call would time out, but the spinning thread would keep running, so the cost would only move. Engines that enforce limits themselves, such as a future Lua or WASM host, will not share this limit.

## Metrics

| Instrument | Tags | Meaning |
|------------|------|---------|
| `twp.plugin.duration` | `plugin` | Seconds one call took, counted for every call. |
| `twp.plugin.errors` | `plugin`, `kind` | Failed calls. `kind` is `exception`, `timeout`, or `unloaded`. |

The CLI exposes the same meter as the other `twp.*` instruments; see the Metrics page in the wiki.

## Contract version

Every plugin class may carry `[ProxyPlugin(version)]`. The host supports the versions from `ProxyPluginContract.MinimumSupportedVersion` to `ProxyPluginContract.CurrentVersion` (both 1 today) and refuses a plugin built for a newer one, naming the plugin in the error. A class without the attribute is treated as version 1. A change to `IProxyMiddleware`, `ProxyMiddlewareDelegate`, or the members of `ProxyMiddlewareContext` that plugins use raises the version, and a unit test pins that surface so it cannot change by accident.

## What is not here, and why

Titanium does not embed a scripting language or a WebAssembly runtime for extensions. A script or module engine adds a second runtime, its own sandbox rules, and a new class of version problems. A .NET plugin has the full type system, the same debugger, and a contract that is checked at load time. Together with the `extAuth` policy (an HTTP service decides) and route transforms (declared rewrites), plugins cover the extension points the proxy needs. If a case is not covered by those three, open an issue describing it.
