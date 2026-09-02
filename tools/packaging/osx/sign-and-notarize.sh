#!/usr/bin/env bash
# Codesign + notarize + staple a macOS artifact (.app, .dmg, or .zip).
# Requires env: APPLE_DEVELOPER_ID, APPLE_CERTIFICATE_P12 (base64), APPLE_CERTIFICATE_PASSWORD,
#               NOTARY_KEY, NOTARY_KEY_ID, NOTARY_ISSUER
set -euo pipefail

TARGET="${1:?path to .app / .dmg / .zip}"
ENTITLEMENTS="${2:-}"

if [[ -z "${APPLE_CERTIFICATE_P12:-}" || -z "${APPLE_DEVELOPER_ID:-}" ]]; then
  echo "skip: Apple signing secrets not configured"
  exit 0
fi

KEYCHAIN="twp-signing.keychain-db"
KEYCHAIN_PW="$(openssl rand -base64 24)"
CERT_PATH="$(mktemp).p12"
trap 'rm -f "${CERT_PATH}"; security delete-keychain "${KEYCHAIN}" 2>/dev/null || true' EXIT

echo "${APPLE_CERTIFICATE_P12}" | base64 --decode > "${CERT_PATH}"
security create-keychain -p "${KEYCHAIN_PW}" "${KEYCHAIN}"
security set-keychain-settings -lut 21600 "${KEYCHAIN}"
security unlock-keychain -p "${KEYCHAIN_PW}" "${KEYCHAIN}"
security import "${CERT_PATH}" -P "${APPLE_CERTIFICATE_PASSWORD}" -A -t cert -f pkcs12 -k "${KEYCHAIN}"
security list-keychain -d user -s "${KEYCHAIN}" $(security list-keychain -d user | tr -d '"')
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "${KEYCHAIN_PW}" "${KEYCHAIN}"

SIGN_ARGS=(--force --options runtime --timestamp --sign "${APPLE_DEVELOPER_ID}")
if [[ -n "${ENTITLEMENTS}" && -f "${ENTITLEMENTS}" ]]; then
  SIGN_ARGS+=(--entitlements "${ENTITLEMENTS}")
fi

if [[ -d "${TARGET}" && "${TARGET}" == *.app ]]; then
  # Sign nested dylibs/frameworks first, then the app.
  find "${TARGET}/Contents" -type f \( -name '*.dylib' -o -name '*.so' -o -perm +111 \) 2>/dev/null \
    | while read -r f; do
        file -b "${f}" 2>/dev/null | grep -qiE 'Mach-O|library|executable' || continue
        codesign "${SIGN_ARGS[@]}" "${f}" || true
      done
  codesign "${SIGN_ARGS[@]}" --deep "${TARGET}"
  codesign --verify --verbose=2 "${TARGET}"
elif [[ -f "${TARGET}" ]]; then
  codesign "${SIGN_ARGS[@]}" "${TARGET}"
  codesign --verify --verbose=2 "${TARGET}" || true
else
  echo "error: unsupported target ${TARGET}" >&2
  exit 1
fi

if [[ -n "${NOTARY_KEY:-}" && -n "${NOTARY_KEY_ID:-}" && -n "${NOTARY_ISSUER:-}" ]]; then
  KEY_FILE="$(mktemp).p8"
  printf '%s\n' "${NOTARY_KEY}" > "${KEY_FILE}"
  SUBMIT="${TARGET}"
  if [[ -d "${TARGET}" ]]; then
    SUBMIT="$(mktemp -d)/notarize.zip"
    ditto -c -k --keepParent "${TARGET}" "${SUBMIT}"
  fi
  xcrun notarytool submit "${SUBMIT}" --wait \
    --key "${KEY_FILE}" --key-id "${NOTARY_KEY_ID}" --issuer "${NOTARY_ISSUER}"
  rm -f "${KEY_FILE}"
  if [[ "${TARGET}" == *.dmg || "${TARGET}" == *.app || -d "${TARGET}" ]]; then
    xcrun stapler staple "${TARGET}" || true
  fi
fi

echo "Signed ${TARGET}"
