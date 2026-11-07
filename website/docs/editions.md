# Editions & licenses

**CLI**, core engine, and this website are **MIT**. **Inspector**, **Plus**, and the Plus satellites (**Titanium.Xds**, **Titanium.Cli.GeoIp**) are optional PolyForm Noncommercial add-ons; license details are in [License](#license) below.

| Product | Role | How you get it |
|---------|------|----------------|
| **CLI** (`titanium` / `twp`) | Standalone reverse / edge proxy for any stack | [Download](/download) (all OS); optional Homebrew on macOS |
| **Inspector** | Desktop man-in-the-middle (MITM) debugger — decrypt and inspect HTTPS traffic | [Download](/download) (Windows / macOS / Linux) |
| **Plus** | Control plane, ops, observability, dashboard, gRPC-JSON, xDS ADS client, GeoIP, response cache, fleet rate limit, active health | After installing CLI: `titanium update --plus` |
| **Library** | Embed the same engine in a .NET app | NuGet (`Titanium.Web.Proxy`) |

CLI and Plus target reverse-proxy / edge workloads (routing, load balancing, health, discovery) on Windows, Linux, and macOS. Inspector is the MITM debugging product. The Library is for embedding the same engine in a .NET process.

## License

The Library and CLI are [MIT](https://github.com/justcoding121/titanium-web-proxy/blob/develop/LICENSE). Inspector, Plus, the xDS satellite (`Titanium.Xds`), and the GeoIP satellite (`Titanium.Cli.GeoIp`) are [PolyForm Noncommercial 1.0.0](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt). Vendored xDS protocol definitions keep Apache-2.0 attribution in `src/Titanium.Xds/NOTICE`.

Plus, Inspector, and the xDS / GeoIP satellites are for non-commercial use only.

Content under `/website` is MIT (see [`website/LICENSE`](https://github.com/justcoding121/titanium-web-proxy/blob/develop/website/LICENSE)). That does not relicense Plus, Inspector, or the PolyForm satellite source.
