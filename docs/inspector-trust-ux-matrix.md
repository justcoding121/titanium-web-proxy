# Inspector trust / proxy UX matrix

Living scorecard for Capture → Install / Remove / Clear+Install / Decrypt / System proxy / Trust Firefox.
Fill outcomes during manual attended CryptUI/Keychain runs; automated Yes/No/Cancel leaves are covered by `Inspector-Trust-Decision` + Headless suites.

## Invariants

| # | Rule |
|---|------|
| 1 | UI never blocks on Root Find, WinINET/scutil, Firefox prefs, or certutil (`RunOffUiAsync` / TrustBg). |
| 2 | CryptUI / Keychain / polkit stay on a pumping UI thread — never `Task.Run`. |
| 3 | Decrypt stays unchecked until trust succeeds (except optimistic already-running+trusted). |
| 4 | Every Cancel / No / fail → model + OneWay snap + visible status/toast. |
| 5 | No Firefox `user.js` / locked prefs I/O while Firefox is running. |
| 6 | TrustBg bounded (drop pending; Clear/Enable timeout). |
| 7 | Platform honesty — Linux ≠ Keychain; Windows Trust Firefox ≠ NSS certutil PATH. |

## Flow × platform × outcome

Legend: **A** = automated (`ScriptedInspectorDialogs` + in-memory trust); **M** = manual/attended OS prompt; **—** = N/A.

| Flow | Outcome | Win | macOS | Linux | Thread lane | Decrypt / SystemProxy | Status / toast | TrustBg |
|------|---------|-----|-------|-------|-------------|------------------------|----------------|---------|
| Start (persisted Decrypt, untrusted) | auto | A | A | A | off-UI refresh | Decrypt force off + snap | Ready / decrypt off | — |
| System proxy enable | Yes | A | A | A | off-UI | optimistic then snap on fail | success / fail toast | — |
| System proxy | PAC Cancel | A | A | A | UI dialog | unchanged / snap | cancelled toast | — |
| ProxyLoopback reapply | fail | A | A | A | off-UI | revert + snap | fail toast | — |
| Decrypt on | Start-proxy No | A | A | A | UI | off + snap | cancelled | — |
| Decrypt on | Install-CA No | A | A | A | UI | off + snap | cancelled | — |
| Decrypt on | CryptUI No / Cancelled | A* | A* | A* | UI OS prompt | off + snap | cancelled | — |
| Decrypt on | recovery Cancel | A | A | A | UI | off + snap | cancelled | busy gate |
| Decrypt on | Mac wait Trusted | — | A/M | — | UI | on | Decrypting HTTPS | Enable best-effort |
| Decrypt on | Mac wait NotSavedYet/Cancel | — | A/M | — | UI | off + snap | Keychain copy | — |
| Decrypt on | Linux incomplete | — | — | A | UI | NSS/certutil recovery (not Keychain) | Export CA / tools | — |
| Install CA | already trusted | A | A | A | — | unchanged | trusted toast | — |
| Install CA | CryptUI Yes | M | M | M | UI | unchanged | trusted toast | Enable bg |
| Install CA | CryptUI No | M | M | M | UI | unchanged | cancelled | — |
| Remove CA | Confirm No | A | A | A | UI | unchanged | cancelled toast | — |
| Remove CA | Confirm Yes + Delete declined | M | M | M | UI | **Decrypt force off** (product rule) | removed / still present | Clear bg |
| Clear+Install | ConfirmRotate No | A | A | A | UI | unchanged | cancelled toast | — |
| Clear+Install | ConfirmRotate Yes → CryptUI | A*/M | M | M | UI (no ConfirmInstall) | Decrypt force off | trusted toast | Await before Remove only |
| Clear+Install | immediate 2nd Yes | A*/M | M | M | UI | Decrypt off | trusted toast | drop pending Clear |
| Trust Firefox | Windows enterprise ok | A | — | — | off-UI | — | restart Firefox | — |
| Trust Firefox | Windows fail (no certutil PATH) | A | — | — | off-UI | — | quit FF / Export CA | — |
| Trust Firefox | FF running → Quit Yes/No | A | A | A | UI + off-UI | — | quit / cancelled | — |
| Trust Firefox | Linux CertutilMissing | — | A | A | UI recovery | — | brew/apt / Export | — |
| Busy gate | Decrypt while Install | A | A | A | — | snap + busy toast | in progress | — |

\* In-memory / `TITANIUM_SKIP_ROOT_STORE_UI` scripts CryptUI as Cancelled/Ok without native Security Warning.

## Attended CryptUI smoke (not CI) — Phase 5d

On a developer Windows box (clear `TITANIUM_SKIP_ROOT_STORE_UI`):

1. Start proxy → **Install root CA** → CryptUI **Yes** → success toast; ux-trace shows `InstallRootStoresOnly.CryptUI` END.
2. **Remove root CA** → Confirm Yes → CryptUI Delete **Yes** → Decrypt off; toast matches store state.
3. Optional: CryptUI **No** on Install → cancelled toast; Decrypt stays off.

Do **not** loop CryptUI in unattended automation.

## Headless Confirm chrome (Phase 5c)

`E2E-UI-Headless` clicks `ConfirmAccept` / `ConfirmCancel` / `TrustRecovery*` via Avalonia Headless + in-memory trust (see MenuActions + Trust decision Headless tests).

## Expected under load

| Marker | Budget |
|--------|--------|
| `AwaitTrustBg` | ≤2s wait; TimeoutOrCancel OK |
| `TrustBg.Job` Clear/Enable with Firefox open | no SLOW ≥3s (skip prefs I/O) |
| Mint crypto | allow ~1–2s |
| Gap Mint → CryptUI | ConfirmInstall skipped on rotate; no TrustBg await |
