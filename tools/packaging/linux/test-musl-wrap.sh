#!/bin/sh
# Proves the musl launcher names icu-libs when ICU is missing, and runs the binary when it is present.
set -eu
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
here=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
printf '%s\n' '#!/bin/sh' 'echo RAN' > "$root/titanium"
cp "$root/titanium" "$root/twp"
chmod +x "$root/titanium" "$root/twp"
sh "$here/wrap-musl-cli.sh" "$root"
if TITANIUM_ICU_PROBE=missing "$root/titanium" >"$root/out" 2>"$root/err"; then
  echo "error: missing ICU should fail" >&2
  exit 1
fi
grep -q "apk add icu-libs" "$root/err"
TITANIUM_ICU_PROBE=present "$root/titanium" | grep -q RAN
echo PASS
