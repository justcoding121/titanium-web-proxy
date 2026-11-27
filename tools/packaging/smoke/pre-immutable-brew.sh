#!/usr/bin/env bash
# brew audit the shipping formula, then install a copy whose url is a local file.
# The file:// install catches a formula that audits but cannot unpack the zip.
# Usage: pre-immutable-brew.sh <cli-zip>
set -euo pipefail
zip="${1:?cli zip}"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
formula="$root/tools/packaging/homebrew/titanium.rb"
if ! command -v brew >/dev/null 2>&1; then
  NONINTERACTIVE=1 /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  if [[ -x /home/linuxbrew/.linuxbrew/bin/brew ]]; then
    eval "$(/home/linuxbrew/.linuxbrew/bin/brew shellenv)"
  elif [[ -x /opt/homebrew/bin/brew ]]; then
    eval "$(/opt/homebrew/bin/brew shellenv)"
  fi
fi
export HOMEBREW_NO_AUTO_UPDATE=1
export HOMEBREW_NO_ENV_HINTS=1
sha="$(sha256sum "$zip" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$zip" | awk '{print $1}')"
abs="$(cd "$(dirname "$zip")" && pwd)/$(basename "$zip")"

# Prefer RELEASE_VERSION from the workflow; otherwise ask the zip's titanium binary.
smoke_ver="${RELEASE_VERSION:-}"
if [[ -z "$smoke_ver" ]]; then
  extract="$(mktemp -d)"
  unzip -qo "$zip" -d "$extract"
  bin="$(find "$extract" -type f \( -name titanium -o -name titanium.exe \) | head -1)"
  if [[ -z "$bin" ]]; then
    echo "ASSERT: could not find titanium in $zip and RELEASE_VERSION is unset" >&2
    exit 1
  fi
  chmod +x "$bin" || true
  # First token that looks like a product version (e.g. 8.0.8 or 8.0.9-beta).
  smoke_ver="$("$bin" version 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+([-.][A-Za-z0-9]+)?' | head -1 || true)"
  rm -rf "$extract"
fi
if [[ -z "$smoke_ver" ]]; then
  echo "ASSERT: could not derive smoke formula version" >&2
  exit 1
fi
# Formula test matches the numeric prefix of a prerelease tag.
smoke_assert="${smoke_ver%%-*}"

work="$(mktemp -d)"
tap="$work/homebrew-smoke"
mkdir -p "$tap/Formula"
cp "$formula" "$tap/Formula/titanium.rb"
cat > "$tap/Formula/titanium-smoke.rb" <<EOF
class TitaniumSmoke < Formula
  desc "Titanium Web Proxy CLI local smoke"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  version "$smoke_ver"
  url "file://$abs"
  sha256 "$sha"
  def install
    libexec.install Dir["*"]
    bin.install_symlink libexec/"titanium"
    bin.install_symlink libexec/"twp" if (libexec/"twp").exist?
  end
  test do
    assert_match "$smoke_assert", shell_output("#{bin}/titanium version")
  end
end
EOF
git -C "$tap" init -q
git -C "$tap" add Formula
git -C "$tap" -c user.email=smoke@localhost -c user.name=smoke commit -qm smoke
brew tap --force local/smoke "$tap"
brew trust local/smoke
brew audit --strict --formula local/smoke/titanium
brew install --formula local/smoke/titanium-smoke
echo "homebrew local install ok"
brew_uninstall_rc=0
brew uninstall --formula local/smoke/titanium-smoke || brew_uninstall_rc=$?
if [[ "$brew_uninstall_rc" -ne 0 ]]; then
  echo "ASSERT: brew uninstall titanium-smoke failed (exit $brew_uninstall_rc)" >&2
  exit 1
fi
if command -v titanium >/dev/null 2>&1; then
  echo "ASSERT: titanium still on PATH after brew uninstall" >&2
  which titanium >&2 || true
  exit 1
fi
brew untap --force local/smoke || true
echo "homebrew uninstall ok"

prev_zip="${PREV_CLI_ZIP:-${2:-}}"
if [[ -n "$prev_zip" && -f "$prev_zip" ]]; then
  prev_ver="${PREV_RELEASE_VERSION:-}"
  if [[ -z "$prev_ver" ]]; then
    prev_extract="$(mktemp -d)"
    unzip -qo "$prev_zip" -d "$prev_extract"
    prev_bin="$(find "$prev_extract" -type f -name titanium | head -1)"
    if [[ -n "$prev_bin" ]]; then
      chmod +x "$prev_bin" || true
      prev_ver="$("$prev_bin" version 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+([-.][A-Za-z0-9]+)?' | head -1 || true)"
    fi
    rm -rf "$prev_extract"
  fi
  if [[ -z "$prev_ver" ]]; then
    echo "skip brew upgrade (could not determine previous formula version)"
    exit 0
  fi
  if [[ "$prev_ver" == "$smoke_ver" ]]; then
    echo "skip brew upgrade (previous formula version equals candidate)"
    exit 0
  fi
  prev_assert="${prev_ver%%-*}"
  prev_sha="$(sha256sum "$prev_zip" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$prev_zip" | awk '{print $1}')"
  prev_abs="$(cd "$(dirname "$prev_zip")" && pwd)/$(basename "$prev_zip")"
  work2="$(mktemp -d)"
  tap2="$work2/homebrew-smoke"
  mkdir -p "$tap2/Formula"
  cat > "$tap2/Formula/titanium.rb" <<EOF
class Titanium < Formula
  desc "Titanium Web Proxy CLI local smoke"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  version "$prev_ver"
  url "file://$prev_abs"
  sha256 "$prev_sha"
  def install
    libexec.install Dir["*"]
    bin.install_symlink libexec/"titanium"
    bin.install_symlink libexec/"twp" if (libexec/"twp").exist?
  end
  test do
    assert_match "$prev_assert", shell_output("#{bin}/titanium version")
  end
end
EOF
  git -C "$tap2" init -q
  git -C "$tap2" add Formula
  git -C "$tap2" -c user.email=smoke@localhost -c user.name=smoke commit -qm prev
  brew tap --force local/smoke "$tap2"
  brew trust local/smoke
  brew install --formula local/smoke/titanium
  cat > "$tap2/Formula/titanium.rb" <<EOF
class Titanium < Formula
  desc "Titanium Web Proxy CLI local smoke"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  version "$smoke_ver"
  url "file://$abs"
  sha256 "$sha"
  def install
    libexec.install Dir["*"]
    bin.install_symlink libexec/"titanium"
    bin.install_symlink libexec/"twp" if (libexec/"twp").exist?
  end
  test do
    assert_match "$smoke_assert", shell_output("#{bin}/titanium version")
  end
end
EOF
  git -C "$tap2" add Formula
  git -C "$tap2" -c user.email=smoke@localhost -c user.name=smoke commit -qm next
  brew update --quiet
  brew upgrade --formula local/smoke/titanium
  got="$(titanium version 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+([-.][A-Za-z0-9]+)?' | head -1 || true)"
  if [[ "${got%%-*}" != "$smoke_assert" ]]; then
    echo "ASSERT: brew upgrade path version '$got' != candidate '$smoke_ver'" >&2
    exit 1
  fi
  brew uninstall --formula local/smoke/titanium || true
  brew untap --force local/smoke || true
  echo "homebrew upgrade-from-published ok ($prev_ver -> $smoke_ver)"
else
  echo "skip brew upgrade (PREV_CLI_ZIP unset or missing; document skip in logs)"
fi
