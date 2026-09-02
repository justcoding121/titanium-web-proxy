#!/usr/bin/env bash
# Build .deb and .rpm from a self-contained publish folder (glibc linux-x64 / linux-arm64).
# Uses fpm when available; falls back to a minimal dpkg-deb for .deb only.
set -euo pipefail

PRODUCT="${1:?cli|inspector}"
PAYLOAD_DIR="${2:?payload dir}"
OUT_DIR="${3:?output directory}"
VERSION="${4:-0.0.0}"
RID="${5:-linux-x64}" # linux-x64 | linux-arm64

PAYLOAD_DIR="$(cd "${PAYLOAD_DIR}" && pwd)"
OUT_DIR="$(mkdir -p "${OUT_DIR}" && cd "${OUT_DIR}" && pwd)"
VERSION="${VERSION%%-*}"

case "${RID}" in
  *arm64*) DEB_ARCH=arm64; RPM_ARCH=aarch64 ;;
  *) DEB_ARCH=amd64; RPM_ARCH=x86_64 ;;
esac

case "${PRODUCT}" in
  cli)
    NAME="titanium-cli"
    EXE="titanium"
    DESC="Titanium Web Proxy CLI"
    ;;
  inspector)
    NAME="titanium-inspector"
    EXE="TitaniumInspector"
    DESC="Titanium Inspector MITM debugger"
    ;;
  *)
    echo "error: product must be cli or inspector" >&2
    exit 1
    ;;
esac

if [[ ! -f "${PAYLOAD_DIR}/${EXE}" ]]; then
  echo "error: ${EXE} missing under ${PAYLOAD_DIR}" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT
PREFIX="${STAGE}/opt/${NAME}"
mkdir -p "${PREFIX}" "${STAGE}/usr/bin"
rsync -a "${PAYLOAD_DIR}/" "${PREFIX}/"
chmod +x "${PREFIX}/${EXE}" 2>/dev/null || true
# PATH symlink
ln -sf "/opt/${NAME}/${EXE}" "${STAGE}/usr/bin/${EXE}"
if [[ "${PRODUCT}" == "cli" && -f "${PREFIX}/twp" ]]; then
  ln -sf "/opt/${NAME}/twp" "${STAGE}/usr/bin/twp"
fi

if [[ "${PRODUCT}" == "inspector" ]]; then
  mkdir -p "${STAGE}/usr/share/applications" "${STAGE}/usr/share/icons/hicolor/256x256/apps"
  DESKTOP_IN="${PAYLOAD_DIR}/TitaniumInspector.desktop.in"
  if [[ -f "${DESKTOP_IN}" ]]; then
    sed -e "s|@EXEC@|/opt/${NAME}/${EXE}|g" -e "s|@ICON@|titanium-inspector|g" \
      "${DESKTOP_IN}" > "${STAGE}/usr/share/applications/titanium-inspector.desktop"
  fi
fi

DEB_OUT="${OUT_DIR}/${NAME}_${VERSION}_${DEB_ARCH}.deb"
RPM_OUT="${OUT_DIR}/${NAME}-${VERSION}-1.${RPM_ARCH}.rpm"

if command -v fpm >/dev/null 2>&1; then
  fpm -s dir -t deb -n "${NAME}" -v "${VERSION}" -a "${DEB_ARCH}" \
    --description "${DESC}" --license "PolyForm-Noncommercial-1.0.0" \
    --url "https://github.com/justcoding121/titanium-web-proxy" \
    -C "${STAGE}" -p "${DEB_OUT}" \
    opt usr
  fpm -s dir -t rpm -n "${NAME}" -v "${VERSION}" -a "${RPM_ARCH}" \
    --description "${DESC}" --license "PolyForm-Noncommercial-1.0.0" \
    --url "https://github.com/justcoding121/titanium-web-proxy" \
    -C "${STAGE}" -p "${RPM_OUT}" \
    opt usr
else
  # Minimal .deb without fpm
  DEB_ROOT="$(mktemp -d)"
  mkdir -p "${DEB_ROOT}/DEBIAN"
  cp -a "${STAGE}/." "${DEB_ROOT}/"
  cat > "${DEB_ROOT}/DEBIAN/control" <<EOF
Package: ${NAME}
Version: ${VERSION}
Section: devel
Priority: optional
Architecture: ${DEB_ARCH}
Maintainer: Jehonathan Thomas <jehonathan@live.com>
Description: ${DESC}
Depends: libnuma1
EOF
  dpkg-deb --build "${DEB_ROOT}" "${DEB_OUT}"
  rm -rf "${DEB_ROOT}"
  echo "warn: fpm not installed — skipped .rpm (${RPM_OUT})" >&2
fi

# Rename to release convention used by download.data.ts
case "${PRODUCT}" in
  cli)
    [[ -f "${DEB_OUT}" ]] && cp -f "${DEB_OUT}" "${OUT_DIR}/Titanium.Cli-${RID}.deb"
    [[ -f "${RPM_OUT}" ]] && cp -f "${RPM_OUT}" "${OUT_DIR}/Titanium.Cli-${RID}.rpm"
    ;;
  inspector)
    [[ -f "${DEB_OUT}" ]] && cp -f "${DEB_OUT}" "${OUT_DIR}/TitaniumInspector-${RID}.deb"
    [[ -f "${RPM_OUT}" ]] && cp -f "${RPM_OUT}" "${OUT_DIR}/TitaniumInspector-${RID}.rpm"
    ;;
esac

echo "Built packages under ${OUT_DIR}"
ls -la "${OUT_DIR}"/*."${DEB_ARCH}".deb "${OUT_DIR}"/*."${RPM_ARCH}".rpm \
  "${OUT_DIR}/Titanium."*-"${RID}".deb "${OUT_DIR}/TitaniumInspector-${RID}".* \
  "${OUT_DIR}/Titanium.Cli-${RID}".* 2>/dev/null || true
