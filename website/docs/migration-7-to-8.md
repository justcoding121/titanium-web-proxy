# Migrating from Titanium Web Proxy 7.x to 8.0

## What stays the same

- Existing `twp.yaml` / JSON configs with informational `schemaVersion: "7.1"` (or earlier additive shapes) still load. Unknown keys remain ignored.
- MIT NuGet packages remain **Titanium.Web.Proxy**, **Titanium.Web.Proxy.Abstractions**, and **Titanium.Web.Proxy.Configuration**. Target framework is still **.NET 10**.
- ForwardHost / terminate-lite behavior is unchanged when you do not configure a route table.
- `Titanium.Web.Proxy.Scripting` is **not** published to nuget.org (CLI-embedded only), same distribution model as Plus.

## Editions

| Product | Role |
|---------|------|
| **Titanium.Cli** (`titanium` / `twp`) | MIT daemon — reverse / forward / edge proxy |
| **Titanium.Plus** | Optional bundle beside the CLI (Plus, xDS, and GeoIP satellites); not on nuget.org; requires Abstractions ≥ **8.0.0** |
| **Titanium Inspector** | Desktop MITM debugger |
| **titanium-gateway** | Gateway API / Ingress controller (GHCR + Helm) |
| **titanium-manager** | Fleet control-plane (GHCR + Helm) |

See the README [License](https://github.com/justcoding121/titanium-web-proxy/blob/develop/README.md#license) section for licenses and distribution channels.

## Plus bundle and ABI floor

8.0 Plus reports `RequiredAbstractionsVersion` **8.0.0**. Pair 8.0 Plus with an 8.0 CLI (or host that loads Abstractions ≥ 8.0.0). Mixing 7.x CLI with 8.0 Plus (or the reverse) skips Plus load.

Plus ships as a **bundle**, not a single plugin DLL:

- `titanium update --plus` prefers `products.plus.bundle` in the release manifest (a zip of `Titanium.Plus.dll`, the PolyForm **Titanium.Xds** and **Titanium.Cli.GeoIp** satellites, and managed dependencies that are not already in the MIT CLI zip). It falls back to the legacy single-DLL `products.plus.asset` for older feeds.
- The MIT CLI zip does not include those satellites. `titanium update --remove-plus` deletes the files listed in `Titanium.Plus.files`.
- The RFC 9111 response cache and active health probing run inside Plus. The MIT core keeps the seams only. `clusters[].healthCheck.active` stays inactive until Plus loads a probe provider.

Features gated behind `plus.enabled` include xDS ADS, GeoIP, response cache, fleet-wide rate limit, and active health probes. Enable Plus before relying on those surfaces.

On Kubernetes the default GHCR image is the MIT CLI and has no shell, so `titanium update --plus` cannot install the bundle in a pod. Use an image that already contains the bundle. See [Plus on Kubernetes](/docs/kubernetes#plus-on-kubernetes).

## Scripting (Lua / WASM)

- Route / stream plugins use `plugins[].kind: lua` or `wasm` (see [Scripting](/docs/scripting)).
- CLI RID zips and the default Linux GHCR image (`ghcr.io/justcoding121/titanium-web-proxy`) ship `liblua54` / `libwasmtime` (or Windows DLL names). Verify with `titanium scripting-deps status`.
- Alpine / musl: use the `linux-musl-*` zip (or a custom image); the glibc container image is not a musl drop-in.
- `win-arm64` scripting natives are not supported.
- Distro / Homebrew `lua` packages do **not** provide the Titanium host shim.

## Kubernetes and Helm

- Charts live under `deploy/helm/{titanium,titanium-gateway,titanium-manager}`.
- Install from the git tree, from Helm `.tgz` assets on the GitHub Release, or from OCI (`oci://ghcr.io/justcoding121/charts`).
- Chart `appVersion` tracks the product patch (**8.0.2**); chart semver is **1.0.2** for this release (the 8.0 packaging contract stays on the **1.x** line).
- Default CLI image includes scripting natives; it still omits `libmsquic` (no HTTP/3 in the default image) and omits the Plus bundle.

## Managed fleet vs Gateway / Ingress

Do not enable Plus managed control-plane mode on the same process that runs as Gateway API or Ingress controller. Managed apply and controller ownership conflict; the product falls back to advisory and surfaces drift — prefer one ownership model per data plane.

## Security tightening (operational)

- Dashboard OIDC with allow-lists: enabling OIDC without group/email allow-lists fails closed.
- JWKS refresh failures fail closed until trust is restored.
- Fail-closed extAuth / stricter CIDR validation may reject configs or requests that 7.x accepted loosely — fix allow-lists and trusted-proxy CIDRs rather than disabling checks.

## Hot restart

`titanium restart` hands over TCP listeners without a gap. HTTP/3 and UDP handover remain deferred; plan a brief gap for those listeners or roll pods instead.

## Library embedders

- Continue to use the three MIT NuGet packages. For Lua/WASM inside an embedder host, copy natives beside the app (same layout as the CLI zip) or run `tools/packaging/bundle-scripting-native.ps1`; there is no Scripting package on nuget.org in 8.0.
- See also [migration 6→7](https://github.com/justcoding121/titanium-web-proxy/blob/develop/docs/migration-6-to-7.md) if you are still on 6.x.
