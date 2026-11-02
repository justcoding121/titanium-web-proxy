# Step 0b firewall clean slate

Applied on this PC on 2026-11-02 from an elevated PowerShell window the user approved.

## Removed

The apply removed **116** ungrouped program rules: testhost 72, rpsloadprobe 16, examples.basic 10, wpf 4, dotnet 4, quic-bind-repro 2, q.exe 2, titaniuminspector 2, windowsservice 2, titanium.exe 2. Needs review was 0. Stock groups and third-party rules were not removed.

Full policy backup: `C:\Work\qa-prompt-audit\fw-backup\firewall-before.wfw` (454656 bytes). The candidate JSON is in the same folder.

## Verification

- A later dry run lists **0** candidates and **0** needs-review rules.
- Persistent rules: 593 total, 558 still in stock groups.
- Microsoft Edge, node.exe, and Cursor rules are still present.
- Domain, Private, and Public profiles remain Enabled, inbound Block, `NotifyOnListen` True.
