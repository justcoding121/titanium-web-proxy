# Step 0b firewall clean slate

Dry run on 2026-11-02 listed **116** candidates and **0** needs-review rules: testhost 72, rpsloadprobe 16, examples.basic 10, wpf 4, dotnet 4, quic-bind-repro 2, q.exe 2, titaniuminspector 2, windowsservice 2, titanium.exe 2. The four extra testhost rules and the q.exe rules are Block rules from Cancel clicks during the audit.

`-Apply` without elevation exits 2. No `.wfw` backup was written. Profiles stayed Enabled, inbound Block, `NotifyOnListen` True. Stock groups and third-party rules were not touched.

This step is waiting on one elevated command, which this session must not launch:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Work\Repositories\titanium-web-proxy\tools\test-hygiene\Remove-TestFirewallRules.ps1" -Apply -BackupDir "C:\Work\qa-prompt-audit\fw-backup"
```

Cancel clicks during the audit added Block rules for the audit testhost paths and a temp QUIC process. The same command removes those too, because they are ungrouped `testhost.exe` rules. After it runs, a dry run must list zero candidates.
