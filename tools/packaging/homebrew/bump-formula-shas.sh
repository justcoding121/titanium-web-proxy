#!/usr/bin/env bash
# Print Homebrew formula snippet with SHA256s from a release-manifest or zip paths.
# Usage: ./bump-formula-shas.sh <version> <osx-arm64.zip> <osx-x64.zip>
set -euo pipefail
VERSION="${1:?version}"
ARM_ZIP="${2:?osx-arm64 zip}"
X64_ZIP="${3:?osx-x64 zip}"
ARM_SHA="$(shasum -a 256 "${ARM_ZIP}" | awk '{print $1}')"
X64_SHA="$(shasum -a 256 "${X64_ZIP}" | awk '{print $1}')"
ROOT="$(cd "$(dirname "$0")" && pwd)"
sed -e "s/version \".*\"/version \"${VERSION}\"/" \
    -e "s/REPLACE_OSX_ARM64_SHA256/${ARM_SHA}/" \
    -e "s/REPLACE_OSX_X64_SHA256/${X64_SHA}/" \
    "${ROOT}/titanium.rb"
