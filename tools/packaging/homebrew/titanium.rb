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
  version "8.0.8"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-arm64.zip"
      sha256 "c70219e705a863eee0ab094b586291aa9aca8354d9b91ee8269419930f8d02b2"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-osx-x64.zip"
      sha256 "f37a6e978de8514b304e291a10c53199deb680c9d66b77f1622cc854dae2a4ef"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-linux-arm64.zip"
      sha256 "493f00e54dfad410b52fc1559237e55a4a2b2687a946f59a1baa652c0fa4f295"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v#{version}/Titanium.Cli-linux-x64.zip"
      sha256 "52496f8698da6a01c45eb18456eed7bfec2b5aa96e55eff43170d9c7a1d1883e"
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
