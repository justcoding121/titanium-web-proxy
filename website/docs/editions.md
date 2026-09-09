# Editions & licenses

Core, CLI, and this website are **MIT**. Inspector and Plus are optional add-ons; license details are in [License](#license) below.

| Product | Role | How you get it |
|---------|------|----------------|
| **Titanium.Cli** (`titanium` / `twp`) | Standalone reverse / edge proxy for any stack, managed by the command line interface (CLI) | [Download](/download) zips, winget, Chocolatey |
| **Titanium Inspector** | Desktop man-in-the-middle (MITM) debugger | [Download](/download) (Windows / macOS / Linux), winget, Chocolatey |
| **Titanium.Plus** | Control plane, ops, observability, dashboard, gRPC-JSON transcoding | After installing CLI: `titanium update --plus` |
| **Titanium.Web.Proxy** | Optional embeddable library for .NET apps | NuGet |

CLI and Plus target reverse-proxy / edge workloads (routing, load balancing, health, discovery) on Windows, Linux, and macOS. Inspector is the MITM debugging product. The Core library is for embedding the same engine in a .NET process.

## License

Titanium.Web.Proxy and Titanium.Cli are [MIT](https://github.com/justcoding121/titanium-web-proxy/blob/develop/LICENSE). Titanium Inspector and Titanium.Plus are [PolyForm Noncommercial 1.0.0](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt).

Plus and Inspector may be used for personal, research, education, government, and charity purposes. Commercial use of those two products needs a separate agreement. They are not a paid SKU in the open repository.

Content under `/website` is MIT (see [`website/LICENSE`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/website/LICENSE)). That does not relicense Plus or Inspector source.
