Automated tests are the merge gate. The engineer writes the test with the change. An agent may author or review tests, and it does not decide whether a pull request passes.

Run one project on Windows with a machine-state check:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-tests.ps1 -Project xds -Verify
```

`tools/run-tests.ps1 -Project all` runs the same projects CI runs, one at a time, with a 5 minute hang blame. Certificate prompts and UAC relaunches are suppressed. A test that binds a wildcard address is inconclusive unless `TITANIUM_ALLOW_WILDCARD_BIND_TESTS=1`.

Pull requests run unit, integration, configuration, Plus, Gateway, scripting, Manager, CLI, xDS, and GeoIP tests, plus the three-OS CLI and UI jobs. Kind install, protocol conformance, and soak stay on a nightly or release path so a slow red check is not ignored.

`tests/coverage-floors.json` is the line-coverage ratchet. Editing it in a pull request is the visible waiver when a floor has to move. Sonar upload does not fail the build.

`.github/required-checks.json` is the only list of merge checks. On `beta` and `stable` the ruleset requires `merge-gate` (the `.NET` build, Redis fleet, and three-OS UI and CLI jobs), the three-OS RPS spot check, secret scan, dependency review, tests-or-waiver, the path guard, and the CLA check. `develop` does not enforce that list. Setting `CONFORMANCE_ENFORCE` or `GATEWAY_CONFORMANCE_ENFORCE` after a green nightly, and applying the ruleset, is a maintainer step. CI does not change those repository settings.

Live Inspector decrypt on a developer PC still needs a person if the OS asks to install a root. CI uses an ephemeral machine and `machine-trust`, not a developer trust store.

## Native dependencies in CI

- Pull request: the surface works, or a dedicated gate fails. HTTP/3 runs on the Windows `build` job (in-box MsQuic, `TITANIUM_REQUIRE_QUIC=1`) and on `ui-portable` for Windows, Linux, and macOS via `.github/actions/ensure-msquic`. `cli-e2e` does not pre-install MsQuic, so `http3-deps install` exercises the real install path. Scripting natives are bundled beside the CLI in `cli-e2e`. Redis runs in `redis-fleet` with `TITANIUM_REQUIRE_REDIS=1`.
- Release: the shipped zip loads its natives. `smoke-http3` and `smoke-scripting` both gate `publish-release`.
- Nightly: soak, the RPS lab, inspector-unattended, and conformance. Conformance stays soft until `CONFORMANCE_ENFORCE` is set.
