#!/usr/bin/env bash
# Required browsers for a product-smoke GUI leg. Extended adds the nightly set.
# Usage: install-required-browsers.sh
set -euo pipefail
mode="${BROWSERS:-required}"
os="$(uname -s)"
arch="$(uname -m)"

if [[ "$os" == "Linux" ]]; then
  sudo apt-get update
  sudo apt-get install -y xvfb xdotool libnss3-tools curl ca-certificates wget gnupg libgtk-3-bin
  sudo install -d -m 0755 /etc/apt/keyrings
  wget -q https://packages.mozilla.org/apt/repo-signing-key.gpg -O- | sudo tee /etc/apt/keyrings/packages.mozilla.org.asc >/dev/null
  echo "deb [signed-by=/etc/apt/keyrings/packages.mozilla.org.asc] https://packages.mozilla.org/apt mozilla main" | sudo tee /etc/apt/sources.list.d/mozilla.list >/dev/null
  printf 'Package: *\nPin: origin packages.mozilla.org\nPin-Priority: 1000\n' | sudo tee /etc/apt/preferences.d/mozilla >/dev/null
  sudo apt-get update
  sudo apt-get install -y --allow-downgrades firefox || true
  firefox --version
  if [[ "$arch" == "aarch64" || "$arch" == "arm64" ]]; then
    chrome_deb=https://dl.google.com/linux/direct/google-chrome-stable_current_arm64.deb
  else
    chrome_deb=https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb
  fi
  if ! command -v google-chrome >/dev/null && ! command -v google-chrome-stable >/dev/null; then
    wget -q -O /tmp/chrome.deb "$chrome_deb"
    sudo apt-get install -y /tmp/chrome.deb
  fi
  if [[ "$mode" == "extended" && "$arch" != "aarch64" && "$arch" != "arm64" ]]; then
    if ! command -v microsoft-edge >/dev/null; then
      curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor | sudo tee /usr/share/keyrings/microsoft.gpg >/dev/null
      echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/edge stable main" | sudo tee /etc/apt/sources.list.d/microsoft-edge.list
      sudo apt-get update
      sudo apt-get install -y microsoft-edge-stable
    fi
  fi
fi

if [[ "$os" == "Darwin" ]]; then
  install_cask() {
    local cask="$1" i
    for i in 1 2 3; do
      brew list --cask "$cask" >/dev/null 2>&1 && return 0
      brew install --cask "$cask" && return 0
      sleep 8
    done
    echo "ASSERT: failed to install $cask" >&2
    return 1
  }
  install_cask firefox
  install_cask google-chrome
  if [[ "$mode" == "extended" ]]; then
    install_cask microsoft-edge || true
  fi
fi
