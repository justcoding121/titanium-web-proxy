# Homebrew formula template for Titanium CLI (custom tap).
# After each release, bump url + sha256 for the active macOS/Linux arch zip.
# Tap usage:
#   brew tap justcoding121/titanium
#   brew install titanium
#
# Place this file in a homebrew-titanium tap repo as Formula/titanium.rb
# (kept here as the source of truth for CI bump scripts).

class Titanium < Formula
  desc "Titanium Web Proxy CLI (MITM / reverse proxy)"
  homepage "https://github.com/justcoding121/titanium-web-proxy"
  version "8.0.7-beta"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-arm64.zip"
      sha256 "7380bd4952c3a2d66e0f1c0ebde846e1f9eadfe85c32a9515c286dac220b93b1"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-x64.zip"
      sha256 "8f0e6201f2b42340d12fb58d3902b64a882060333c44ef05b565191d7584abc4"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-linux-arm64.zip"
      sha256 "b6d28994ee5fb1b426637ea47a0eb7d9a8b88972f70eab8d35758bdef6ddecdc"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-linux-x64.zip"
      sha256 "2f460db2bc19cb7ffcf1e24ab44a2125381651d675fd3914c0143c39f3b46e39"
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