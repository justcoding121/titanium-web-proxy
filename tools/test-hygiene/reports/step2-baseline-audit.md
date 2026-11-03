# Step 2 baseline audit

Run on this PC on 2026-11-02 against a detached worktree at `f24e5e34`:

`C:\Work\qa-prompt-audit\829f216d467f4d4bbbb716a322c6e969`

`CI`, `GITHUB_ACTIONS`, and `TF_BUILD` were not set. Firewall profiles were left as they were (`Enabled`, inbound Block, `NotifyOnListen` True). Step 0b was not applied: the dry run still lists the old prompt-created rules, and removing them needs an elevated shell. The audit path itself had no rule, so a new executable there can still raise an alert.

Findings are in `C:\Work\qa-prompt-audit\findings.jsonl`.

## Outcomes

| Project | Total | Passed | Failed | Not executed |
| --- | --- | --- | --- | --- |
| configuration | 335 | 334 | 0 | 1 |
| scripting | 105 | 68 | 0 | 37 |
| manager | 29 | 28 | 0 | 1 |
| gateway | 291 | 290 | 0 | 1 |
| xds | 11 | 11 | 0 | 0 |
| geoip | 3 | 3 | 0 | 0 |
| unit | 2885 | 2867 | 0 | 18 |
| cli | 349 | 346 | 0 | 3 |
| plus | 460 | 458 | 0 | 2 |
| inspector | 711 | 710 | 1 | 0 |
| integration, not Slow | 771 | 763 | 0 | 8 |
| integration, Slow | 7 | 7 | 0 | 0 |
| e2e, category E2E | 138 | 123 | 0 | 15 |
| e2e UI categories | 105 | 105 | 0 | 0 |
| e2e UI window | 1 | 1 | 0 | 0 |
| e2e Slow | 10 | 1 | 0 | 9 |

The inspector failure is `InspectorFileLogProviderTests.Prune_LeavesAtMostKeepFiles` (expected at most 2 files, got 3). It is not a prompt. The E2E-Slow pass is `Browsers_BehindExplicitProxy_DoNotOpenDirectQuicAfterAltSvc`. The nine E2E-Slow skips are the real-trust browser and system-proxy tests.

## Findings by cause

- UDP connect without a prior bind is reported as `0.0.0.0` or `::` and raises a firewall alert. Seen in unit UDP relay tests and `UdpOnlyConfig_RunsWithoutAnyHttpListener_ThroughForwardHostAndCluster`. The listener host in that CLI config is `127.0.0.1`.
- `DualModeListener_ServesIPv4Clients` binds `IPv6Any` for a few milliseconds. That test is gated on `TITANIUM_ALLOW_WILDCARD_BIND_TESTS`.
- A QUIC listen whose `LocalEndPoint` is `[::1]` is still reported as `0.0.0.0` and `::`. Windows asks once per executable path. Cancel was clicked for the integration testhost and for a throwaway QUIC process. The same path did not ask again. A tighter bind does not stop this.
- Certificate count for Titanium/TWP subjects rose from 0 to 16 across unit, CLI, Plus, and Inspector on the audited commit. LocalMachine stayed empty. No certificate dialog title was logged.
- No `UAC_PENDING`. `TITANIUM_NO_ELEVATE=1` is set by the shared runsettings, and the service relaunch path does not prompt when that variable is set.

## Dialogs

Three Windows Security dialogs were cancelled by hand during diagnosis and the integration run (main-repo CLI testhost, audit integration testhost, and a temp QUIC process). The watcher on the audit worktree did not match the Windows 11 title "Windows Security"; the main-tree watcher script now includes that title. No dialog was left open. No certificate install was accepted.
