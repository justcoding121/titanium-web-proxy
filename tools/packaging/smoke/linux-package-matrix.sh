#!/usr/bin/env bash
# CLI and Inspector packages for one Linux RID.
# deb/AppImage run on the host. rpm runs in fedora:41. musl zips run in alpine:3.24.
# Usage: linux-package-matrix.sh <dist-dir> <rid>
set -euo pipefail
dist="${1:?dist}"
rid="${2:?rid}"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
here="$(cd "$(dirname "$0")" && pwd)"
log="${RUNNER_TEMP:-/tmp}/pkg-$rid"
mkdir -p "$log"

shopt -s nullglob
debs=("$dist"/Titanium.Cli-"$rid".deb "$dist"/*/"Titanium.Cli-$rid".deb)
rpms=("$dist"/Titanium.Cli-"$rid".rpm "$dist"/*/"Titanium.Cli-$rid".rpm)
images=("$dist"/Titanium.Cli-"$rid".AppImage "$dist"/*/"Titanium.Cli-$rid".AppImage)
zips=("$dist"/Titanium.Cli-"$rid".zip "$dist"/*/"Titanium.Cli-$rid".zip)
idebs=("$dist"/TitaniumInspector-"$rid".deb "$dist"/*/"TitaniumInspector-$rid".deb)
irpms=("$dist"/TitaniumInspector-"$rid".rpm "$dist"/*/"TitaniumInspector-$rid".rpm)
iimages=("$dist"/TitaniumInspector-"$rid".AppImage "$dist"/*/"TitaniumInspector-$rid".AppImage)
izips=("$dist"/TitaniumInspector-"$rid".zip "$dist"/*/"TitaniumInspector-$rid".zip)

first() { for f in "$@"; do [[ -f "$f" ]] && echo "$f" && return 0; done; return 1; }

if cli_deb="$(first "${debs[@]}")"; then
  bin="$("$here/install-cli.sh" "$cli_deb" "$log/cli-deb")"
  "$here/retry.sh" --leg assert -- "$here/cli-mitm.sh" "$bin" "$log/cli-mitm"
fi
if cli_img="$(first "${images[@]}")"; then
  "$here/appimage-run.sh" "$cli_img" "$log/cli-appimage"
fi
if ins_img="$(first "${iimages[@]}")"; then
  "$here/appimage-run.sh" "$ins_img" "$log/inspector-appimage"
fi
if cli_zip="$(first "${zips[@]}")"; then
  if [[ "$rid" == linux-musl-* ]]; then
    docker run --rm -v "$root:/src" -v "$(dirname "$cli_zip"):/pkg" -w /src "alpine:3.24" \
      sh -c "apk add --no-cache bash && bash tools/packaging/smoke/alpine-deps.sh && unzip -q /pkg/$(basename "$cli_zip") -d /cli && chmod +x /cli/titanium && /cli/titanium version && bash tools/packaging/smoke/openssl-leftover.sh /cli /cli/titanium"
  fi
fi
if cli_rpm="$(first "${rpms[@]}")"; then
  stage="$(mktemp -d)"
  cp -f "$cli_rpm" "$stage/"
  docker run --rm -v "$stage:/pkg:ro" "fedora:41" \
    bash -c "dnf install -y libicu '/pkg/$(basename "$cli_rpm")' && titanium version"
fi
if ins_deb="$(first "${idebs[@]}")"; then
  if [[ "$rid" == "linux-x64" ]]; then
    browsers=chrome,firefox
    [[ "${BROWSERS:-required}" == "extended" ]] && browsers=chrome,firefox,edge
    "$here/install-required-browsers.sh"
    exe="$("$here/install-inspector.sh" "$ins_deb" "$log/inspector")"
    "$here/retry.sh" --leg gui -- "$here/prove-inspector.sh" "$exe" "$browsers" "$log/inspector-gui"
  else
    exe="$("$here/install-inspector.sh" "$ins_deb" "$log/inspector")"
    "$exe" --version >/dev/null 2>&1 || true
    test -x "$exe"
  fi
fi
if ins_rpm="$(first "${irpms[@]}")"; then
  stage="$(mktemp -d)"
  cp -f "$ins_rpm" "$stage/"
  docker run --rm -v "$stage:/pkg:ro" "fedora:41" \
    bash -c "dnf install -y '/pkg/$(basename "$ins_rpm")' && test -x /opt/titanium-inspector/TitaniumInspector"
fi
if ins_zip="$(first "${izips[@]}")"; then
  if [[ "$rid" == linux-musl-* ]]; then
    docker run --rm -v "$(dirname "$ins_zip"):/pkg" "alpine:3.24" \
      sh -c "apk add --no-cache unzip && unzip -q /pkg/$(basename "$ins_zip") -d /opt/inspector && test -f /opt/inspector/TitaniumInspector"
  fi
fi
echo "linux packages $rid ok"
