# Route plugin sample

`HeaderGatePlugin` is a per-route .NET plugin for the `titanium` CLI. It answers `403` when a request lacks one header and stamps another request header on the requests it lets through.

## Build and use

```powershell
dotnet build -c Release
```

Copy `bin/Release/net10.0/Titanium.Web.Proxy.Examples.RoutePlugin.dll` next to your `twp.yaml` and add:

```yaml
plugins:
  - name: header-gate
    assembly: ./Titanium.Web.Proxy.Examples.RoutePlugin.dll
    settings:
      requiredHeader: X-Api-Client
      stampHeader: X-Route-Plugin
    failMode: closed
    timeoutMs: 200

routes:
  - id: api
    clusterId: backend
    match: { path: /api, pathKind: Prefix }
    middleware: [header-gate]
```

Only `/api` runs the plugin. Every other route is unchanged.

## What to copy

- `[ProxyPlugin(ProxyPluginContract.CurrentVersion)]` declares the contract version the plugin was built for.
- The constructor takes the `settings` map.
- The project references `Titanium.Web.Proxy.Abstractions` and `Titanium.Web.Proxy` with `Private=false` and `ExcludeAssets=runtime`, so the host's own copies are the ones the plugin uses.

The full guide is in `website/docs/plugins.md`.
