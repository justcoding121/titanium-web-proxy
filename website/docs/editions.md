# Editions & licenses

| Product | Role | License | How you get it |
|---------|------|---------|----------------|
| **Titanium.Cli** (`titanium` / `twp`) | Standalone reverse / edge daemon for any stack | MIT | [Download](/download) zips, winget |
| **Titanium Inspector** | Desktop MITM debugger | [PolyForm NC](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt) | MSI / zip / winget |
| **Titanium.Plus** | Control plane, ops, observability, dashboard | PolyForm NC | `titanium update --plus` |
| **Titanium.Web.Proxy** | Optional embeddable library for .NET apps | MIT | NuGet |

CLI and Plus target reverse-proxy / edge workloads (routing, load balancing, health, discovery) on Windows, Linux, and macOS. Inspector is the MITM debugging product. The Core library is for embedding the same engine in a .NET process.

## What PolyForm NC means

Plus and Inspector are **noncommercial** under PolyForm Noncommercial 1.0.0: personal, research, education, government, and charity use are allowed; **commercial use is not** without a separate agreement. They are not a paid SKU in the open repository.

## Website & docs

Content under `/website` is MIT (see [`website/LICENSE`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/website/LICENSE)). That does not relicense Plus or Inspector source.
