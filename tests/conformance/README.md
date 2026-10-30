# Conformance and resilience program

What is checked, where it runs, and where the known gaps are written down. The workflows are
`.github/workflows/conformance.yml` (parser, chaos, h2spec, Autobahn, real peer) and `.github/workflows/soak.yml`.

| Area | What | Where | When |
|---|---|---|---|
| HTTP/1 parser safety | CL.TE, TE.CL, duplicate Content-Length, obs-fold, bare LF, space before colon, oversized headers, invalid chunks; ClientHello SNI parser | `tests/fuzz`, `ParserCorpusRunnerTests` ([fuzz README](../fuzz/README.md)) | every PR (paths-filtered) |
| Reverse-proxy chaos | origin reset mid-body, stall, failure before the response commits, client abort mid-body; bounded retries, no hang | `ReverseProxyChaosTests` | every PR (paths-filtered) |
| Real HTTP server row | reverse proxy to an in-process Kestrel origin | `ReverseProxyTests` | every PR (paths-filtered) |
| Config upgrade compatibility | golden configs from older releases still load and validate | `tests/Titanium.Web.Proxy.Configuration.Tests/Compat`, `ConfigCompatibilityTests` | every PR (paths-filtered) |
| HTTP/2 conformance | h2spec against a prior-knowledge h2c listener | `conformance.yml` job `h2spec`, allow-list `h2spec-known.txt` | nightly + dispatch |
| WebSocket conformance | Autobahn|Testsuite client cases through a reverse listener | `conformance.yml` job `autobahn`, allow-list `autobahn-known.txt` | nightly + dispatch |
| Real peer | reverse listener in front of a stock nginx container; compares the proxy with the peer | `conformance.yml` job `real-peer` | nightly + dispatch |
| Soak | 60 minutes of constant reverse-proxy load with error, memory and fd gates | `soak.yml` (RpsLoadProbe) | nightly + dispatch |

## Existing coverage that these jobs rely on

Not duplicated here:

- **HTTP/2 abuse budgets** (rapid reset, CONTINUATION flood, incomplete stream resets, open header blocks):
  `Http2ContinuationAndAbuseBudgetTests` in the unit tests.
- **WebSocket**: `WebSocketUpgradeTests`, `WebSocketInterceptionSafetyTests`, and the `Rfc8441*Tests` (extended CONNECT
  tunnels over h1 and h2) in the integration tests.
- **Invalid config reload keeps the old config**: covered in the E2E suite.
- **Origin failure matrix** (which failures become which status code): `OriginFailureMatrixTests`.

## How the heavy jobs behave

- They run on `schedule` and `workflow_dispatch` only, never on a pull request.
- If the external tool cannot be fetched (h2spec download, Docker image pull), the job prints a `::notice::` and ends
  green. Nothing silently passes: the skipped steps show as skipped.
- They report from the first run on, but only fail the workflow once the repository variable `CONFORMANCE_ENFORCE`
  is `true` (the same pattern as `gateway-conformance.yml`). Flip it after the first nightly baselines are recorded in
  the allow-lists.
- None of these jobs has been run on a GitHub runner yet. The first scheduled or dispatched run is the check of the
  recipe; expect to adjust the allow-lists and perhaps a flag.

## Allow-lists

`h2spec-known.txt` and `autobahn-known.txt` list failures that are accepted for now. One entry per line; `#` starts a
comment. Every entry needs an issue link and a reason, and goes away when the gap is fixed (the checker,
`check-known.ps1`, notices entries that no longer fail). A failure that no entry covers fails the check.

- h2spec: an entry is a substring of `<package> | <classname> | <name>` of the failing JUnit test case, for example
  `http2/6.5.3`.
- Autobahn: an entry is an exact case id, for example `6.4.3`.

Run the checker locally against a report:

```powershell
pwsh tests/conformance/check-known.ps1 -Kind h2spec -Report report.xml -Known tests/conformance/h2spec-known.txt
```

## Running a heavy job by hand

```powershell
dotnet publish src/Titanium.Cli/Titanium.Cli.csproj -c Release -o artifacts/titanium
artifacts/titanium/titanium run -c tests/conformance/configs/h2spec.yaml     # origin on 9100, listener on 9101
h2spec -h 127.0.0.1 -p 9101 -j report.xml
```

`configs/autobahn.yaml` (listener 9102 to a fuzzingserver on 9001) and `configs/real-peer.yaml` (listener 9202 to nginx on
9201) work the same way. The Autobahn client is a single file: `dotnet run tests/conformance/autobahn/AutobahnClient.cs
-- ws://127.0.0.1:9102 titanium`.

## Soak assertions

Documented at the top of `soak.yml`: zero-ish errors and the probe's own SLO verdict, bounded RSS growth between the
first and last third of the window, bounded file-descriptor growth, and a live proxy at the end. Throughput is recorded
and not gated.
