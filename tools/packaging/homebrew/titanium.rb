# Homebrew formula template for Titanium CLI (custom tap).
# After each release, bump url + sha256 for the active macOS/Linux arch zip.
# Tap usage:
#   brew tap justcoding121/titanium
#   brew install titanium
#
# Place this file in a homebrew-titanium tap repo as Formula/titanium.rb
# (kept here as the source of truth for CI bump scripts).

class Titanium < Formula
  desc "MITM and reverse proxy command-line"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  version "8.0.8-beta"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-arm64.zip"
      sha256 "af36d26dc9b3cd9a6818b52ddd0e3fcbee320e3671f33a7c3dd47a31ab271add"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-x64.zip"
      sha256 "7494f928513fe871fcee4eb4d4bcf0bc6c33995e1b8e53652115d09e532136b2"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-linux-arm64.zip"
      sha256 "9e9c93c77a5ed3fcc73b89ee5c384b00909594e18f74eb94848cafb7d0552345"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-linux-x64.zip"
      sha256 "67aa209e642cd52c4f1dfe37f0bc199a436c3aea8980cb76e33dba50afdeb2ac"
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
