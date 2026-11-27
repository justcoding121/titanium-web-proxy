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
allow_missing="${ALLOW_MISSING:-0}"

# download-artifact v4+ with a pattern nests as dist/<dir>/<artifact-name>/file,
# so search the whole tree (not only one directory level).
find_pkgs() {
  local name="$1"
  find "$dist" -type f -name "$name" 2>/dev/null | sort
}

mapfile -t debs < <(find_pkgs "Titanium.Cli-$rid.deb")
mapfile -t rpms < <(find_pkgs "Titanium.Cli-$rid.rpm")
mapfile -t images < <(find_pkgs "Titanium.Cli-$rid.AppImage")
mapfile -t zips < <(find_pkgs "Titanium.Cli-$rid.zip")
mapfile -t idebs < <(find_pkgs "TitaniumInspector-$rid.deb")
mapfile -t irpms < <(find_pkgs "TitaniumInspector-$rid.rpm")
mapfile -t iimages < <(find_pkgs "TitaniumInspector-$rid.AppImage")
mapfile -t izips < <(find_pkgs "TitaniumInspector-$rid.zip")

first() { for f in "$@"; do [[ -f "$f" ]] && echo "$f" && return 0; done; return 1; }

missing_required() {
  local label="$1"
  if [[ "$allow_missing" == "1" ]]; then
    echo "::warning::missing $label for $rid (ALLOW_MISSING=1)" >&2
    return 1
  fi
  echo "missing required package: $label (rid=$rid, dist=$dist)" >&2
  exit 1
}

require_first() {
  local label="$1"
  shift
  local path
  if path="$(first "$@")"; then
    printf '%s\n' "$path"
    return 0
  fi
  missing_required "$label"
  return 1
}

if [[ "$rid" == linux-x64 || "$rid" == linux-arm64 ]]; then
  require_first "cli deb" "${debs[@]}" >/dev/null
  require_first "cli rpm" "${rpms[@]}" >/dev/null
  require_first "cli AppImage" "${images[@]}" >/dev/null
  require_first "inspector deb" "${idebs[@]}" >/dev/null
  require_first "inspector rpm" "${irpms[@]}" >/dev/null
  require_first "inspector AppImage" "${iimages[@]}" >/dev/null
elif [[ "$rid" == linux-musl-* ]]; then
  require_first "cli zip" "${zips[@]}" >/dev/null
  require_first "inspector zip" "${izips[@]}" >/dev/null
fi

if cli_deb="$(first "${debs[@]}")"; then
  if [[ -n "${PREV_CLI_DEB:-}" && -f "$PREV_CLI_DEB" ]]; then
    bash "$here/cli-package-lifecycle.sh" deb "$PREV_CLI_DEB" "$cli_deb" "$rid" "${LIFECYCLE_PROOF:-}"
  else
    echo "skip cli deb package lifecycle (PREV_CLI_DEB unset)"
  fi
  bin="$("$here/install-cli.sh" "$cli_deb" "$log/cli-deb")"
  "$here/retry.sh" --leg assert -- "$here/cli-mitm.sh" "$bin" "$log/cli-mitm"
  # Uninstall restores nothing for CLI (no system-proxy flag); assert package gone.
  sudo dpkg -r titanium-cli >/dev/null 2>&1 || sudo dpkg -r titanium-cli || true
  if [[ -x /opt/titanium-cli/titanium ]]; then
    echo "ASSERT: CLI deb left /opt/titanium-cli/titanium after dpkg -r" >&2
    exit 1
  fi
  if [[ -n "${LIFECYCLE_PROOF:-}" ]]; then
    bash "$here/append-lifecycle-proof.sh" "$LIFECYCLE_PROOF" cli "$rid" deb uninstall "" "" "" true
  fi
  echo "cli deb uninstall ok"
fi
if cli_img="$(first "${images[@]}")"; then
  "$here/appimage-run.sh" "$cli_img" "$log/cli-appimage"
  # AppImage is a single file; removal is deleting the file.
  if [[ ! -f "$cli_img" ]]; then
    echo "ASSERT: CLI AppImage missing before removal check" >&2
    exit 1
  fi
  echo "cli AppImage present ok"
fi
if ins_img="$(first "${iimages[@]}")"; then
  if [[ -n "${PREV_INS_APPIMAGE:-}" && -f "$PREV_INS_APPIMAGE" ]]; then
    bash "$here/enforce.sh" inspector-appimage-lifecycle -- \
      bash "$here/inspector-package-lifecycle.sh" appimage "$PREV_INS_APPIMAGE" "$ins_img" "$rid" "${LIFECYCLE_PROOF:-}"
  else
    echo "skip inspector AppImage package lifecycle (PREV_INS_APPIMAGE unset)"
    "$here/appimage-run.sh" "$ins_img" "$log/inspector-appimage"
    echo "inspector AppImage present ok"
  fi
fi
if cli_zip="$(first "${zips[@]}")"; then
  if [[ "$rid" == linux-musl-* ]]; then
    stage="$(mktemp -d)"
    # Distinct names: PREV and candidate share basename Titanium.Cli-<rid>.zip;
    # copying both as basename overwrote the candidate with the published 8.0.8 zip
    # and alpine ran the old binary (Update feed network error under HTTPS_PROXY).
    cand_zip_name="candidate-$(basename "$cli_zip")"
    cp -f "$cli_zip" "$stage/$cand_zip_name"
    prev_zip_arg=""
    upgrade_cmd="echo 'skip musl cli-upgrade-from-published (PREV_CLI_ZIP unset)'"
    if [[ -n "${PREV_CLI_ZIP:-}" && -f "$PREV_CLI_ZIP" ]]; then
      prev_zip_name="prev-$(basename "$PREV_CLI_ZIP")"
      cp -f "$PREV_CLI_ZIP" "$stage/$prev_zip_name"
      prev_zip_arg="/pkg/$prev_zip_name"
      upgrade_cmd="bash tools/packaging/smoke/cli-upgrade-from-published.sh '$prev_zip_arg' '/pkg/$cand_zip_name' /tmp/cli-musl-upgrade"
    fi
    # Same alpine update proof for musl-x64 and musl-arm64. Earlier arm64 skips chased an
    # HttpClient+HTTPS_PROXY theory that was actually PREV/candidate zip basename overwrite.
    update_in_alpine="bash tools/packaging/smoke/cli-update-ignores-proxy.sh /cli '/pkg/$cand_zip_name' /cli/titanium"
    docker run --rm -v "$root:/src" -v "$stage:/pkg:ro" -w /src "alpine:3.24" \
      sh -c "apk add --no-cache bash unzip && bash tools/packaging/smoke/alpine-deps.sh && unzip -q '/pkg/$cand_zip_name' -d /cli && chmod +x /cli/titanium && /cli/titanium version && bash tools/packaging/smoke/openssl-leftover.sh /cli /cli/titanium && $update_in_alpine && $upgrade_cmd"
    if [[ -n "${LIFECYCLE_PROOF:-}" && -n "$prev_zip_arg" ]]; then
      bash "$here/append-lifecycle-proof.sh" "$LIFECYCLE_PROOF" cli "$rid" zip upgrade \
        "published" "${RELEASE_VERSION:-candidate}" "${RELEASE_VERSION:-candidate}" false
    fi
  fi
fi
if cli_rpm="$(first "${rpms[@]}")"; then
  if [[ -n "${PREV_CLI_RPM:-}" && -f "$PREV_CLI_RPM" ]]; then
    bash "$here/cli-package-lifecycle.sh" rpm "$PREV_CLI_RPM" "$cli_rpm" "$rid" "${LIFECYCLE_PROOF:-}"
  else
    echo "skip cli rpm package lifecycle (PREV_CLI_RPM unset)"
    stage="$(mktemp -d)"
    cp -f "$cli_rpm" "$stage/"
    docker run --rm -v "$stage:/pkg:ro" "fedora:41" \
      bash -c "dnf install -y libicu '/pkg/$(basename "$cli_rpm")' && titanium version && rpm -e titanium-cli && ! test -x /opt/titanium-cli/titanium && echo 'cli rpm uninstall ok'"
  fi
fi
if ins_deb="$(first "${idebs[@]}")"; then
  if [[ -n "${PREV_INS_DEB:-}" && -f "$PREV_INS_DEB" ]]; then
    bash "$here/enforce.sh" inspector-deb-lifecycle -- \
      bash "$here/inspector-package-lifecycle.sh" deb "$PREV_INS_DEB" "$ins_deb" "$rid" "${LIFECYCLE_PROOF:-}"
  else
    echo "skip inspector deb package lifecycle (PREV_INS_DEB unset)"
  fi
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
  if [[ -n "${PREV_INS_RPM:-}" && -f "$PREV_INS_RPM" ]]; then
    bash "$here/enforce.sh" inspector-rpm-lifecycle -- \
      bash "$here/inspector-package-lifecycle.sh" rpm "$PREV_INS_RPM" "$ins_rpm" "$rid" "${LIFECYCLE_PROOF:-}"
  else
    echo "skip inspector rpm package lifecycle (PREV_INS_RPM unset)"
    stage="$(mktemp -d)"
    cp -f "$ins_rpm" "$stage/"
    docker run --rm -v "$stage:/pkg:ro" "fedora:41" \
      bash -c "dnf install -y '/pkg/$(basename "$ins_rpm")' && test -x /opt/titanium-inspector/TitaniumInspector && rpm -e titanium-inspector && ! test -x /opt/titanium-inspector/TitaniumInspector && echo 'inspector rpm uninstall ok'"
    if [[ -n "${LIFECYCLE_PROOF:-}" ]]; then
      bash "$here/append-lifecycle-proof.sh" "$LIFECYCLE_PROOF" inspector "$rid" rpm uninstall "" "" "" true
    fi
  fi
fi
if ins_zip="$(first "${izips[@]}")"; then
  if [[ "$rid" == linux-musl-* ]]; then
    if [[ -n "${PREV_INS_ZIP:-}" && -f "$PREV_INS_ZIP" ]]; then
      bash "$here/enforce.sh" inspector-musl-zip-lifecycle -- \
        bash "$here/inspector-package-lifecycle.sh" zip "$PREV_INS_ZIP" "$ins_zip" "$rid" "${LIFECYCLE_PROOF:-}"
    else
      echo "skip inspector musl zip package lifecycle (PREV_INS_ZIP unset)"
      stage="$(mktemp -d)"
      cp -f "$ins_zip" "$stage/"
      docker run --rm -v "$stage:/pkg:ro" "alpine:3.24" \
        sh -c "apk add --no-cache unzip && unzip -q '/pkg/$(basename "$ins_zip")' -d /opt/inspector && test -f /opt/inspector/TitaniumInspector && rm -rf /opt/inspector && ! test -f /opt/inspector/TitaniumInspector && echo 'inspector musl zip remove ok'"
      if [[ -n "${LIFECYCLE_PROOF:-}" ]]; then
        bash "$here/append-lifecycle-proof.sh" "$LIFECYCLE_PROOF" inspector "$rid" zip uninstall "" "" "" true
      fi
    fi
  fi
fi
echo "linux packages $rid ok"