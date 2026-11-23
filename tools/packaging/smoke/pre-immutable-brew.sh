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
brew audit --strict --formula "$formula"
sha="$(sha256sum "$zip" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$zip" | awk '{print $1}')"
abs="$(cd "$(dirname "$zip")" && pwd)/$(basename "$zip")"
work="$(mktemp -d)"
cat > "$work/titanium-smoke.rb" <<EOF
class TitaniumSmoke < Formula
  desc "Titanium Web Proxy CLI local smoke"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  version "8.0.7"
  url "file://$abs"
  sha256 "$sha"
  def install
    libexec.install Dir["*"]
    bin.install_symlink libexec/"titanium"
    bin.install_symlink libexec/"twp" if (libexec/"twp").exist?
  end
  test do
    assert_match "8.0.7", shell_output("#{bin}/titanium version")
  end
end
EOF
brew install --formula "$work/titanium-smoke.rb"
echo "homebrew local install ok"
