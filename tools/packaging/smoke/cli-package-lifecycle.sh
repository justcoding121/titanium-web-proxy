#!/usr/bin/env bash
# deb/rpm CLI package upgrade then remove (expects PREV_* packages from a published release).
#
# Usage:
#   cli-package-lifecycle.sh deb <prev.deb> <candidate.deb> <rid> [proof.json]
#   cli-package-lifecycle.sh rpm <prev.rpm> <candidate.rpm> <rid> [proof.json]
#   cli-package-lifecycle.sh --self-test
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"

self_test() {
  bash -n "$0"
  echo "cli-package-lifecycle self-test ok"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

kind="${1:?deb or rpm}"
shift
prev="${1:?prev package}"
candidate="${2:?candidate package}"
rid="${3:?rid}"
proof="${4:-}"

prev="$(cd "$(dirname "$prev")" && pwd)/$(basename "$prev")"
candidate="$(cd "$(dirname "$candidate")" && pwd)/$(basename "$candidate")"

if [[ ! -f "$prev" || ! -f "$candidate" ]]; then
  echo "ASSERT: prev or candidate package missing" >&2
  exit 1
fi

deb_version_file() { dpkg-deb -f "$1" Version 2>/dev/null; }
rpm_version_file() { rpm -qp --queryformat '%{VERSION}' "$1" 2>/dev/null; }

append_proof() {
  local phase="$1" from_v="$2" to_v="$3" after_v="$4" restored="$5"
  [[ -n "$proof" ]] || return 0
  bash "$here/append-lifecycle-proof.sh" "$proof" cli "$rid" "$kind" "$phase" \
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
    prev_installed="$(dpkg-query -W -f='${Version}' titanium-cli 2>/dev/null || true)"
    if [[ -z "$prev_installed" ]]; then
      echo "ASSERT: titanium-cli not registered after prev deb install" >&2
      exit 1
    fi
    sudo dpkg -i "$candidate" >&2 || sudo apt-get install -y -f >&2
    installed="$(dpkg-query -W -f='${Version}' titanium-cli)"
    if [[ "$installed" != "$to_v" ]]; then
      echo "ASSERT: dpkg-query version '$installed' != candidate '$to_v'" >&2
      exit 1
    fi
    test -x /opt/titanium-cli/titanium
    /opt/titanium-cli/titanium version >&2
    append_proof upgrade "$from_v" "$to_v" "$installed" false
    sudo dpkg -r titanium-cli >&2
    if [[ -x /opt/titanium-cli/titanium ]]; then
      echo "ASSERT: CLI deb left /opt/titanium-cli/titanium after dpkg -r" >&2
      exit 1
    fi
    append_proof uninstall "$from_v" "$to_v" "" true
    echo "cli deb package lifecycle ok ($from_v -> $to_v, removed)"
    ;;
  rpm)
    from_v="$(rpm_version_file "$prev")"
    to_v="$(rpm_version_file "$candidate")"
    if [[ -z "$from_v" || -z "$to_v" ]]; then
      echo "ASSERT: could not read rpm VERSION fields" >&2
      exit 1
    fi
    stage="$(mktemp -d)"
    # Distinct names: prev and candidate share basename Titanium.Cli-<rid>.rpm.
    cp -f "$prev" "$stage/prev.rpm"
    cp -f "$candidate" "$stage/candidate.rpm"
    docker run --rm -v "$stage:/pkg:ro" "fedora:41" \
      bash -c "set -euo pipefail
        dnf install -y -q libicu /pkg/prev.rpm
        prev_i=\$(rpm -q --queryformat '%{VERSION}' titanium-cli)
        test -n \"\$prev_i\"
        dnf install -y -q /pkg/candidate.rpm
        want='$to_v'
        got=\$(rpm -q --queryformat '%{VERSION}' titanium-cli)
        test \"\$got\" = \"\$want\"
        test -x /opt/titanium-cli/titanium
        /opt/titanium-cli/titanium version
        rpm -e titanium-cli
        test ! -x /opt/titanium-cli/titanium
        echo \"cli rpm package lifecycle ok ($from_v -> $to_v, removed)\""
    append_proof upgrade "$from_v" "$to_v" "$to_v" false
    append_proof uninstall "$from_v" "$to_v" "" true
    ;;
  *)
    echo "ASSERT: unknown kind $kind (deb|rpm)" >&2
    exit 1
    ;;
esac