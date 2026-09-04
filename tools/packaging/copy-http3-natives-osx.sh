#!/usr/bin/env bash
# Copy Homebrew MsQuic + OpenSSL into an output folder with @loader_path so
# System.Net.Quic.QuicListener.IsSupported works for local Debug/dotnet run builds
# (Release zips use bundle-http3-native.ps1 instead).
set -euo pipefail

OUT_DIR="${1:-}"
if [[ -z "$OUT_DIR" || ! -d "$OUT_DIR" ]]; then
  echo "[http3-copy] usage: $0 <output-directory>" >&2
  exit 2
fi

OUT_DIR="$(cd "$OUT_DIR" && pwd)"

resolve_brew_prefix() {
  if [[ -n "${HOMEBREW_PREFIX:-}" && -d "${HOMEBREW_PREFIX}/opt/libmsquic/lib" ]]; then
    echo "$HOMEBREW_PREFIX"
    return
  fi
  for p in "${HOME}/.homebrew" /opt/homebrew /usr/local; do
    if [[ -d "$p/opt/libmsquic/lib" ]]; then
      echo "$p"
      return
    fi
  done
  if command -v brew >/dev/null 2>&1; then
    brew --prefix
    return
  fi
  return 1
}

PREFIX="$(resolve_brew_prefix || true)"
if [[ -z "${PREFIX}" ]]; then
  echo "[http3-copy] skip: libmsquic not found (brew install libmsquic openssl@3)"
  exit 0
fi

MSQ_LIB="$PREFIX/opt/libmsquic/lib"
SSL_LIB="$PREFIX/opt/openssl@3/lib"
if [[ ! -d "$MSQ_LIB" ]]; then
  echo "[http3-copy] skip: missing $MSQ_LIB"
  exit 0
fi
if [[ ! -d "$SSL_LIB" ]]; then
  echo "[http3-copy] skip: missing $SSL_LIB (brew install openssl@3)"
  exit 0
fi

MSQ_SRC=""
for cand in libmsquic.2.6.1.dylib libmsquic.2.dylib libmsquic.dylib; do
  if [[ -e "$MSQ_LIB/$cand" ]]; then
    MSQ_SRC="$MSQ_LIB/$cand"
    break
  fi
done

if [[ -z "$MSQ_SRC" ]]; then
  echo "[http3-copy] skip: no libmsquic*.dylib under $MSQ_LIB"
  exit 0
fi

# -L follows Homebrew versioned symlinks so install_name_tool edits a real dylib.
cp -fL "$MSQ_SRC" "$OUT_DIR/libmsquic.2.6.1.dylib"
cp -f "$OUT_DIR/libmsquic.2.6.1.dylib" "$OUT_DIR/libmsquic.2.dylib"
cp -f "$OUT_DIR/libmsquic.2.6.1.dylib" "$OUT_DIR/libmsquic.dylib"
cp -fL "$SSL_LIB/libssl.3.dylib" "$OUT_DIR/libssl.3.dylib"
cp -fL "$SSL_LIB/libcrypto.3.dylib" "$OUT_DIR/libcrypto.3.dylib"

rewrite_id() {
  local f="$1"
  install_name_tool -id "@loader_path/$(basename "$f")" "$f" 2>/dev/null || true
}

for f in libmsquic.2.6.1.dylib libmsquic.2.dylib libmsquic.dylib libssl.3.dylib libcrypto.3.dylib; do
  rewrite_id "$OUT_DIR/$f"
done

# Point msquic / libssl at local libcrypto (absolute Homebrew paths or placeholders).
retarget_crypto() {
  local f="$1"
  local dep
  while IFS= read -r dep; do
    [[ -z "$dep" ]] && continue
    case "$dep" in
      *libcrypto*)
        install_name_tool -change "$dep" "@loader_path/libcrypto.3.dylib" "$f" 2>/dev/null || true
        ;;
    esac
  done < <(otool -L "$f" | awk 'NR>1 {print $1}')
}

for f in libmsquic.2.6.1.dylib libmsquic.2.dylib libmsquic.dylib libssl.3.dylib; do
  retarget_crypto "$OUT_DIR/$f"
done

if command -v codesign >/dev/null 2>&1; then
  codesign --force -s - \
    "$OUT_DIR"/libmsquic.2.6.1.dylib \
    "$OUT_DIR"/libmsquic.2.dylib \
    "$OUT_DIR"/libmsquic.dylib \
    "$OUT_DIR"/libssl.3.dylib \
    "$OUT_DIR"/libcrypto.3.dylib >/dev/null 2>&1 || true
fi

echo "[http3-copy] bundled MsQuic + OpenSSL into $OUT_DIR (from $PREFIX)"
