# Step 3 fix loop

The audited findings that can be fixed in code are fixed on `chore/test-no-prompt-hygiene`. The certificate stores were cleaned again after the audit: 23 current-user certificates (17 CA, 6 My), broad scan 0, local-machine candidates 0. Public backups are in `C:\Work\qa-prompt-audit\cert-backup-step3`. `rootCert.pfx` was not touched.

## Fixed

| Finding | Fix | Guard |
| --- | --- | --- |
| UDP connect reported as `0.0.0.0` or `::` | Bind the socket to loopback before connect when the peer is loopback (`UdpRelayListener`, MASQUE) | `LoopbackPeer_BindsTheUpstreamSocketToLoopbackBeforeConnect` |
| Dual-mode `IPv6Any` listen | Inconclusive unless `TITANIUM_ALLOW_WILDCARD_BIND_TESTS=1` | The test itself |
| QUIC client to localhost | Set `LocalEndPoint` to loopback | `QuicLoopbackBindTests` |
| CLI listener host omitted or wildcard | `CliProcessHarness.AssertLoopbackConfig` | `StartRun_RejectsAWildcardListenerHost` |
| Certificate store writes | Root UI stays suppressed. `SslStreamCertificateContext.Create` copies the signer into CurrentUser\CA; a suppressed host removes that copy. WinAuth tests remove what they add. | `CertificateStoreWriteSuppressionTests`, `TwpStoreLeakGuard` |
| Service UAC | `TITANIUM_NO_ELEVATE=1` in runsettings and on every harness child | Existing `CanPromptInteractively` check |

## Residual

A QUIC listen whose `LocalEndPoint` is `[::1]` is still reported by Windows as `0.0.0.0` and `::`. The firewall asks once per new executable path. Binding a tighter address does not stop it. Two full runs at never-used paths are therefore not dialog-free while HTTP/3 tests run. `tools/test-hygiene/Initialize-TestMachine.ps1` can add loopback-only allow rules or turn `NotifyOnListen` off, and it does neither unless the user passes a switch. PromptGuard clicks Cancel on a developer PC.

## Not run

The elevated firewall cleanup in Step 0b. Two consecutive full-suite runs at new worktree paths, because those runs would raise the QUIC dialog above.
