> For operators and library users upgrading from 7.x to 8.0.

## Plus license boundary

8.0 draws a clearer license line in the tree and in the CLI zip:

- The RFC 9111 response cache and `healthCheck.active` probing live in **Titanium.Plus** (PolyForm Noncommercial).
- **Titanium.Xds** and **Titanium.Cli.GeoIp** are PolyForm satellites. They are no longer copied into the MIT CLI zip.
- Install them with `titanium update --plus`, which prefers a **bundle** zip (`products.plus.bundle` in the release manifest) that contains `Titanium.Plus.dll`, the satellites, and managed dependencies that are not already in the CLI zip. Older CLIs that only understand `products.plus.asset` still receive the single DLL.

`plus.enabled` checks in `titanium test` are unchanged: they gate config, they are not the license boundary.

## Library breaking changes

These public types moved out of the MIT `Titanium.Web.Proxy` package:

- `Titanium.Web.Proxy.Caching.HttpResponseCacheMiddleware`
- `Titanium.Web.Proxy.Caching.MemoryHttpResponseCache`
- `Titanium.Web.Proxy.Caching.ResponseCacheOptions`

Library hosts that need the cache reference **Titanium.Plus** (PolyForm) or implement `IHttpResponseCache` / `IResponseCacheEngine` themselves. The MIT core keeps session glue (`CacheSessionSlot`) and Abstractions seams only.

`clusters[].healthCheck.active` is inactive at runtime until an `IActiveHealthProbeProvider` is assigned (Titanium Plus does this when the module loads). DNS refresh (`dnsRefreshSeconds`) stays in core. Prefer `plus.options resilience.activeHealth` for the full Plus resilience path.

## CLI / packaging

- A clean MIT CLI zip no longer includes `Titanium.Xds.dll`, `Titanium.Cli.GeoIp.dll`, or MaxMind/gRPC satellite deps.
- `titanium update --plus` installs the bundle; `titanium update --remove-plus` deletes exactly the files listed in `Titanium.Plus.files` (falls back to `Titanium.Plus*.dll` when the list is missing).
- xDS and GeoIP loaders point operators at `titanium update --plus` when the satellite DLL is missing.

## Config

No new required keys. Features that already needed `plus.enabled` (`cache.enable`, `healthCheck.active`, geo policies, xDS) still do. After upgrading the CLI, run `titanium update --plus` before enabling those features on a machine that only had the MIT zip.
