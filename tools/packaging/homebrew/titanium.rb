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

  version "8.0.9-beta"
  on_macos do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9-beta/Titanium.Cli-osx-arm64.zip"
      sha256 "a8ca4bc2d7e882cb05f844048c79cab31565bd0703158c1d2d927a3c5f788a6b"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9-beta/Titanium.Cli-osx-x64.zip"
      sha256 "ee163a4fb55e8d9eac587399f96c764f95aa6334a6504e4e2a08b9069ae1ad9d"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9-beta/Titanium.Cli-linux-arm64.zip"
      sha256 "b752841032285e5c0e11e89ee2f5ab7d4688cedd274753ebffa1ae5f7f1e63b9"
    end
    on_intel do
      url "https://github.com/justcoding121/titanium-web-proxy/releases/download/v8.0.9-beta/Titanium.Cli-linux-x64.zip"
      sha256 "db1ce96c151be97e4c834d7ce047691877cc53230f7357b3101d34e5635a69b2"
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
