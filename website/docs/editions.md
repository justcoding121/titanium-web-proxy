# Editions & licenses

| Product | Role | License | How you get it |
|---------|------|---------|----------------|
| **Titanium.Web.Proxy** | Embed MITM and/or reverse proxy in a .NET app | MIT | NuGet |
| **Titanium.Cli** (`titanium` / `twp`) | Standalone reverse / edge daemon | MIT | [Download](/download) zips, winget |
| **Titanium.Plus** | Control plane, ops, observability, dashboard | [PolyForm NC](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt) | `titanium update --plus` |
| **Titanium Inspector** | Desktop MITM debugger | PolyForm NC | MSI / zip / winget |

CLI and Plus target reverse-proxy / edge workloads (routing, load balancing, health, discovery). Inspector is the MITM debugging product. The Core library supports both modes.

## What PolyForm NC means

Plus and Inspector are **noncommercial** under PolyForm Noncommercial 1.0.0: personal, research, education, government, and charity use are allowed; **commercial use is not** without a separate agreement. They are not a paid SKU in the open repository.

## Website & docs

Content under `/website` is MIT (see [`website/LICENSE`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/website/LICENSE)). That does not relicense Plus or Inspector source.
