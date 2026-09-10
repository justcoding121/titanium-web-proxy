# Editions & licenses

**CLI**, core engine, and this website are **MIT**. **Inspector** and **Plus** are optional add-ons; license details are in [License](#license) below.

| Product | Role | How you get it |
|---------|------|----------------|
| **CLI** (`titanium` / `twp`) | Standalone reverse / edge proxy for any stack | [Download](/download) zips, winget, Chocolatey |
| **Inspector** | Desktop man-in-the-middle (MITM) debugger — decrypt and inspect HTTPS traffic | [Download](/download) (Windows / macOS / Linux), winget, Chocolatey |
| **Plus** | Control plane, ops, observability, dashboard, gRPC-JSON transcoding | After installing CLI: `titanium update --plus` |
| **Library** | Embed the same engine in a .NET app | NuGet (`Titanium.Web.Proxy`) |

CLI and Plus target reverse-proxy / edge workloads (routing, load balancing, health, discovery) on Windows, Linux, and macOS. Inspector is the MITM debugging product. The Library is for embedding the same engine in a .NET process.

## License

The Library and CLI are [MIT](https://github.com/justcoding121/titanium-web-proxy/blob/develop/LICENSE). Inspector and Plus are [PolyForm Noncommercial 1.0.0](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt).

Plus and Inspector may be used for personal, research, education, government, and charity purposes. Commercial use of those two products needs a separate agreement. They are not a paid SKU in the open repository.

Content under `/website` is MIT (see [`website/LICENSE`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/website/LICENSE)). That does not relicense Plus or Inspector source.
