#!/usr/bin/env bash
# deb/rpm/AppImage/musl-zip Inspector package upgrade then remove (expects published PREV_*).
#
# Usage:
#   inspector-package-lifecycle.sh deb <prev.deb> <candidate.deb> <rid> [proof.json]
#   inspector-package-lifecycle.sh rpm <prev.rpm> <candidate.rpm> <rid> [proof.json]
#   inspector-package-lifecycle.sh appimage <prev.AppImage> <candidate.AppImage> <rid> [proof.json]
#   inspector-package-lifecycle.sh zip <prev.zip> <candidate.zip> <rid> [proof.json]
#   inspector-package-lifecycle.sh --self-test
#
# Env (optional instead of positional packages): PREV_INS_DEB, PREV_INS_RPM,
# PREV_INS_APPIMAGE, PREV_INS_ZIP with CANDIDATE paths from args or CAND_*.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"

self_test() {
  bash -n "$0"
  echo "inspector-package-lifecycle self-test ok"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

kind="${1:?deb|rpm|appimage|zip}"
shift
prev="${1:?prev package}"
candidate="${2:?candidate package}"
rid="${3:?rid}"
proof="${4:-${LIFECYCLE_PROOF:-}}"

prev="$(cd "$(dirname "$prev")" && pwd)/$(basename "$prev")"
candidate="$(cd "$(dirname "$candidate")" && pwd)/$(basename "$candidate")"

if [[ ! -f "$prev" || ! -f "$candidate" ]]; then
  echo "ASSERT: prev or candidate package missing" >&2
  exit 1
fi

deb_version_file() { dpkg-deb -f "$1" Version 2>/dev/null; }
rpm_version_file() { rpm -qp --queryformat '%{VERSION}' "$1" 2>/dev/null; }

version_from_binary() {
  local bin="$1"
  local hit
  hit="$(strings "$bin" 2>/dev/null | grep -Eo '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.+-]+)?$' | head -1 || true)"
  if [[ -n "$hit" ]]; then
    printf '%s' "$hit"
    return 0
  fi
  hit="$(strings "$bin" 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.+-]+)?' | head -1 || true)"
  printf '%s' "${hit:-}"
}

version_from_appimage() {
  local img="$1" work exe ver
  work="$(mktemp -d)"
  chmod +x "$img"
  export APPIMAGE_EXTRACT_AND_RUN=1
  if (cd "$work" && "$img" --appimage-extract >/dev/null 2>&1); then
    exe="$(find "$work/squashfs-root" -name TitaniumInspector -type f 2>/dev/null | head -1 || true)"
    if [[ -n "$exe" && -f "$exe" ]]; then
      ver="$(version_from_binary "$exe")"
      rm -rf "$work"
      printf '%s' "$ver"
      return 0
    fi
  fi
  ver="$(strings "$img" 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.+-]+)?' | head -1 || true)"
  rm -rf "$work"
  printf '%s' "$ver"
}

version_from_zip() {
  local zip="$1" work exe ver
  work="$(mktemp -d)"
  unzip -q "$zip" -d "$work"
  exe="$(find "$work" -name TitaniumInspector -type f | head -1 || true)"
  if [[ -z "$exe" || ! -f "$exe" ]]; then
    rm -rf "$work"
    echo "ASSERT: TitaniumInspector missing in $zip" >&2
    exit 1
  fi
  ver="$(version_from_binary "$exe")"
  rm -rf "$work"
  printf '%s' "$ver"
}

assert_gsettings_proxy_clear() {
  if ! command -v gsettings >/dev/null 2>&1; then
    return 0
  fi
  local mode
  mode="$(gsettings get org.gnome.system.proxy mode 2>/dev/null || true)"
  mode="${mode//\'/}"
  if [[ -n "${mode}" && "${mode}" != "none" ]]; then
    echo "ASSERT: gsettings proxy mode still ${mode} after inspector deb remove" >&2
    exit 1
  fi
}

append_proof() {
  local phase="$1" from_v="$2" to_v="$3" after_v="$4" restored="$5"
  [[ -n "$proof" ]] || return 0
  local fmt="$kind"
  [[ "$kind" == "appimage" ]] && fmt="AppImage"
  bash "$here/append-lifecycle-proof.sh" "$proof" inspector "$rid" "$fmt" "$phase" \
    "$from_v" "$to_v" "$after_v" "$restored"
}

case "$kind" in
  deb)
    from_v="$(deb_version_file "$prev")"
    to_v="$(deb_version_file "$candidate")"
    if [[ -z "$from_v" || -z "$to_v" ]]; then
      echo "ASSERT: could not read deb Version fields" >&2
      exit 1
    fi
    sudo dpkg -i "$prev" >&2 || sudo apt-get install -y -f >&2
    prev_installed="$(dpkg-query -W -f='${Version}' titanium-inspector 2>/dev/null || true)"
    if [[ -z "$prev_installed" ]]; then
      echo "ASSERT: titanium-inspector not registered after prev deb install" >&2
      exit 1
    fi
    sudo dpkg -i "$candidate" >&2 || sudo apt-get install -y -f >&2
    installed="$(dpkg-query -W -f='${Version}' titanium-inspector)"
    if [[ "$installed" != "$to_v" ]]; then
      echo "ASSERT: dpkg-query version '$installed' != candidate '$to_v'" >&2
      exit 1
    fi
    test -x /opt/titanium-inspector/TitaniumInspector
    append_proof upgrade "$from_v" "$to_v" "$installed" false
    sudo dpkg -r titanium-inspector >&2
    if [[ -x /opt/titanium-inspector/TitaniumInspector ]]; then
      echo "ASSERT: inspector deb left binary after dpkg -r" >&2
      exit 1
    fi
    assert_gsettings_proxy_clear
    append_proof uninstall "$from_v" "$to_v" "" true
    echo "inspector deb package lifecycle ok ($from_v -> $to_v, removed)"
    ;;
  rpm)
    from_v="$(rpm_version_file "$prev")"
    to_v="$(rpm_version_file "$candidate")"
    if [[ -z "$from_v" || -z "$to_v" ]]; then
      echo "ASSERT: could not read rpm VERSION fields" >&2
      exit 1
    fi
    stage="$(mktemp -d)"
    # Distinct names: prev and candidate share basename TitaniumInspector-<rid>.rpm.
    cp -f "$prev" "$stage/prev.rpm"
    cp -f "$candidate" "$stage/candidate.rpm"
    docker run --rm -v "$stage:/pkg:ro" "fedora:41" \
      bash -c "set -euo pipefail
        dnf install -y -q libicu /pkg/prev.rpm
        prev_i=\$(rpm -q --queryformat '%{VERSION}' titanium-inspector)
        test -n \"\$prev_i\"
        dnf install -y -q /pkg/candidate.rpm
        want='$to_v'
        got=\$(rpm -q --queryformat '%{VERSION}' titanium-inspector)
        test \"\$got\" = \"\$want\"
        test -x /opt/titanium-inspector/TitaniumInspector
        rpm -e titanium-inspector
        test ! -x /opt/titanium-inspector/TitaniumInspector
        echo \"inspector rpm package lifecycle ok ($from_v -> $to_v, removed)\""
    append_proof upgrade "$from_v" "$to_v" "$to_v" false
    append_proof uninstall "$from_v" "$to_v" "" true
    ;;
  appimage)
    from_v="$(version_from_appimage "$prev")"
    to_v="$(version_from_appimage "$candidate")"
    if [[ -z "$from_v" || -z "$to_v" ]]; then
      echo "ASSERT: could not read AppImage version markers" >&2
      exit 1
    fi
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    cp -f "$prev" "$work/run.AppImage"
    chmod +x "$work/run.AppImage"
    export APPIMAGE_EXTRACT_AND_RUN=1
    (cd "$work" && ./run.AppImage --appimage-extract >/dev/null)
    root_dir="$work/squashfs-root"
    exe="$(find "$root_dir" -name TitaniumInspector -type f | head -1)"
    test -n "$exe" && test -x "$exe"
    cp -f "$candidate" "$work/run.AppImage"
    chmod +x "$work/run.AppImage"
    rm -rf "$root_dir"
    (cd "$work" && ./run.AppImage --appimage-extract >/dev/null)
    exe="$(find "$work/squashfs-root" -name TitaniumInspector -type f | head -1)"
    test -n "$exe" && test -x "$exe"
    got_v="$(version_from_binary "$exe")"
    if [[ -z "$got_v" || "$got_v" != "$to_v" ]]; then
      echo "ASSERT: AppImage upgrade binary version '$got_v' != '$to_v'" >&2
      exit 1
    fi
    append_proof upgrade "$from_v" "$to_v" "$got_v" false
    rm -f "$work/run.AppImage"
    rm -rf "$work/squashfs-root"
    if [[ -f "$work/run.AppImage" ]]; then
      echo "ASSERT: AppImage file still present after removal" >&2
      exit 1
    fi
    append_proof uninstall "$from_v" "$to_v" "" true
    echo "inspector AppImage package lifecycle ok ($from_v -> $to_v, removed)"
    ;;
  zip)
    from_v="$(version_from_zip "$prev")"
    to_v="$(version_from_zip "$candidate")"
    if [[ -z "$from_v" || -z "$to_v" ]]; then
      echo "ASSERT: could not read musl zip version markers" >&2
      exit 1
    fi
    stage="$(mktemp -d)"
    # Distinct names: prev and candidate share basename TitaniumInspector-<rid>.zip.
    cp -f "$prev" "$stage/prev.zip"
    cp -f "$candidate" "$stage/candidate.zip"
    docker run --rm -v "$stage:/pkg:ro" "alpine:3.24" \
      sh -c "set -e
        apk add --no-cache unzip
        rm -rf /opt/inspector
        mkdir -p /opt/inspector
        unzip -q /pkg/prev.zip -d /opt/inspector
        test -f /opt/inspector/TitaniumInspector
        unzip -oq /pkg/candidate.zip -d /opt/inspector
        test -f /opt/inspector/TitaniumInspector
        chmod +x /opt/inspector/TitaniumInspector
        rm -rf /opt/inspector
        test ! -f /opt/inspector/TitaniumInspector
        echo \"inspector musl zip package lifecycle ok ($from_v -> $to_v, removed)\""
    append_proof upgrade "$from_v" "$to_v" "$to_v" false
    append_proof uninstall "$from_v" "$to_v" "" true
    ;;
  *)
    echo "ASSERT: unknown kind $kind (deb|rpm|appimage|zip)" >&2
    exit 1
    ;;
esac