# Homebrew formula template for Titanium CLI (custom tap).
# After each release, bump url + sha256 for the active macOS arch zip.
# Tap usage:
#   brew tap justcoding121/titanium
#   brew install titanium
#
# Place this file in a homebrew-titanium tap repo as Formula/titanium.rb
# (kept here as the source of truth for CI bump scripts).

class Titanium < Formula
  desc "Titanium Web Proxy CLI (MITM / reverse proxy)"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  version "7.0.4"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-arm64.zip"
      sha256 "REPLACE_OSX_ARM64_SHA256"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-x64.zip"
      sha256 "REPLACE_OSX_X64_SHA256"
    end
  end

  def install
    # Keep publish layout so @loader_path / $ORIGIN natives resolve next to the binary.
    libexec.install Dir["*"]
    bin.install_symlink libexec/"titanium"
    bin.install_symlink libexec/"twp" if (libexec/"twp").exist?
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/titanium version")
  end
end
