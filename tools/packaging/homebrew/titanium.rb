# Homebrew formula template for Titanium CLI (custom tap).
# After each release, bump url + sha256 for the active macOS/Linux arch zip.
# Tap usage:
#   brew tap justcoding121/titanium
#   brew install titanium
#
# Place this file in a homebrew-titanium tap repo as Formula/titanium.rb
# (kept here as the source of truth for CI bump scripts).
#
# Stable (X.Y.Z): omit the version stanza; URLs use a literal /vX.Y.Z/ path so
# Homebrew infers version. Prerelease (X.Y.Z-beta): bump-formula-from-sums.sh
# inserts an explicit version stanza (URL scanning is unreliable for suffixes).

class Titanium < Formula
  desc "MITM and reverse proxy command-line"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  license "MIT"
  on_macos do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9/Titanium.Cli-osx-arm64.zip"
      sha256 "27adfbf9b5300ad84fea8581a3e54829d57cec23ffc655d5050ec22b26278df9"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9/Titanium.Cli-osx-x64.zip"
      sha256 "a1f5fec232b36718fa50e9de7d5708db66f6f026fa3e7c02b81a95be7ca95600"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9/Titanium.Cli-linux-arm64.zip"
      sha256 "fb8f9f442d02c24ee9b2971d6f28766d8982cb12884321eefff9f495e478ea24"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9/Titanium.Cli-linux-x64.zip"
      sha256 "e22dc4918ff33e0bcb9356025fb8d9e704005ba022598386edaaa2f264692bf6"
    end
  end

  def install
    # Keep publish layout so @loader_path / $ORIGIN natives resolve next to the binary.
    libexec.install Dir["*"]
    bin.install_symlink libexec/"titanium"
    bin.install_symlink libexec/"twp" if (libexec/"twp").exist?
  end

  test do
    # Assembly versions are numeric (e.g. 7.0.5.0); formula may be 7.0.5-beta.
    assert_match version.to_s.split("-").first, shell_output("#{bin}/titanium version")
  end
end
