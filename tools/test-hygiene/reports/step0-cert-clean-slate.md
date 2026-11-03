# Step 0 certificate clean slate

Run on this PC on 2026-11-02. No machine-store elevation was required.

## What was removed

Dry run matched **702** current-user certificates and **0** local-machine certificates.

| Store | Before | Removed | After |
| --- | --- | --- | --- |
| CurrentUser\Root | 73 | 1 | 72 |
| CurrentUser\My | 382 | 219 | 163 |
| CurrentUser\CA | 630 | 482 | 148 |

Patterns also covered `CN=Titanium Integration Custom Root CA <guid>` and `CN=Titanium Sonar Gate Cov CA`, which the first pattern list missed. A second apply removed the remaining 695 after the first apply stopped on a missing private-key container. The script now falls back to removing the certificate when `-DeleteKey` hits "Keyset does not exist".

Public `.cer` backups and `inventory.json` are in `C:\Work\qa-prompt-audit\cert-backup`. Private keys were not exported. `%LOCALAPPDATA%\Titanium.Web.Proxy\rootCert.pfx` was not touched.

## Verification

- Broad scan (`Titanium|twp-|TWP|Acme Corp`) across Root, My, CA, TrustedPeople, AuthRoot, Disallowed, and TrustedPublisher for CurrentUser and LocalMachine: **0 hits**.
- A second dry run is idempotent: 0 candidates.
- LocalMachine dry run: 0 candidates, so no elevated step.

## Dialog

`certutil -user -delstore` for the product root raised one visible window titled **Root Certificate Store** (class `#32770`) and then completed. The root count dropped from 73 to 72. The run did not hang. No UAC prompt appeared.

## Consequence

This Windows account no longer trusts `CN=Titanium Root Certificate Authority`. Inspector or the CLI will offer to install it again from `rootCert.pfx` the next time Install CA or Decrypt traffic is used. That prompt is product behavior, not a test.

## Leak to fix in Step 3

The 701 certificates besides the product root were left by tests (`WinAuthTests`, `CertificateManagerTests`, `CertificateStoreIsolationTests` / `TestCertificateAuthority`, and the `Titanium UnitTest Root CA` / `Titanium Sonar Gate Cov CA` fixtures). Tests must stop writing to the real user stores.
