# Apple Developer secrets (macOS Gatekeeper)

Agent work (scripts + gated `release.yml`) is ready. **Notarization stays no-op until you finish this checklist** and put secrets in GitHub — do **not** paste `.p12` / `.p8` / private keys into chat.

## A. Developer ID Application certificate

1. [Certificates list](https://developer.apple.com/account/resources/certificates/list) → **+** → **Developer ID Application**.
2. Create a CSR locally (Keychain on Mac, or `openssl req -new -newkey rsa:2048 -nodes -keyout developer_id.key -out developer_id.csr` on Windows).
3. Upload CSR → download `.cer` → export password-protected `.p12` (needs the private key from step 2).
4. Note identity string: `Developer ID Application: Your Name (TEAMID)`.

## B. App Store Connect API key (notarization)

1. [App Store Connect](https://appstoreconnect.apple.com) → Users and Access → Integrations → App Store Connect API.
2. Create a key; download `.p8` once; copy **Issuer ID** and **Key ID**.

## C. GitHub secrets

| Secret | Value |
| --- | --- |
| `APPLE_DEVELOPER_ID` | `Developer ID Application: … (TEAMID)` |
| `APPLE_CERTIFICATE_P12` | base64 of the `.p12` |
| `APPLE_CERTIFICATE_PASSWORD` | p12 password |
| `NOTARY_KEY` | `.p8` contents |
| `NOTARY_KEY_ID` | Key ID |
| `NOTARY_ISSUER` | Issuer ID |

When done, tell the agent: **“Apple secrets are in GitHub”** so a beta release can verify Gatekeeper.
