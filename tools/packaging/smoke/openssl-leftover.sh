#!/usr/bin/env bash
# A leftover libssl/libcrypto beside libmsquic must disappear when titanium starts.
# The "no libmsquic, leave the files" branch is covered by Http3NativeBootstrapTests.
# Usage: openssl-leftover.sh <install-dir> <titanium-bin>
set -euo pipefail
dir="${1:?install dir}"
bin="${2:?titanium}"
if ! compgen -G "$dir/libmsquic.so*" >/dev/null; then
  echo "ASSERT: $dir has no libmsquic.so; leftover cleanup only runs on a Titanium MsQuic layout" >&2
  exit 1
fi
printf 'stale' > "$dir/libssl.so.3"
printf 'stale' > "$dir/libcrypto.so.3"
"$bin" version >/dev/null
if compgen -G "$dir/libssl.so*" >/dev/null || compgen -G "$dir/libcrypto.so*" >/dev/null; then
  echo "ASSERT: titanium left bundled OpenSSL beside libmsquic in $dir" >&2
  exit 1
fi
echo "openssl leftover cleanup ok"
