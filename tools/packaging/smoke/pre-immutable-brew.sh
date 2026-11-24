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
brew uninstall --formula local/smoke/titanium-smoke || true
brew untap --force local/smoke || true
