#!/usr/bin/env bash
# Build .deb and .rpm from a self-contained publish folder (glibc linux-x64 / linux-arm64).
# Uses fpm when available; falls back to a minimal dpkg-deb for .deb only.
set -euo pipefail

PRODUCT="${1:?cli|inspector}"
PAYLOAD_DIR="${2:?payload dir}"
OUT_DIR="${3:?output directory}"
VERSION="${4:-0.0.0}"
RID="${5:-linux-x64}" # linux-x64 | linux-arm64
# Optional UTC stamp (YYYYMMDDHHMMSS). A later stamp sorts as a newer Debian/RPM
# version, so App Center offers Update instead of "already installed".
BUILD_STAMP="${6:-}"

PAYLOAD_DIR="$(cd "${PAYLOAD_DIR}" && pwd)"
OUT_DIR="$(mkdir -p "${OUT_DIR}" && cd "${OUT_DIR}" && pwd)"

# 8.0.4-beta -> 8.0.4~beta (sorts before the release). Always append +stamp so a
# rebuild of the same tag is a higher version than the package already installed.
deb_version() {
  local raw="${1#v}"
  local stamp="$2"
  if [[ -z "${stamp}" ]]; then
    stamp="$(date -u +%Y%m%d%H%M%S)"
  fi
  local upstream="${raw}"
  if [[ "${raw}" == *-* ]]; then
    upstream="${raw%%-*}~${raw#*-}"
  fi
  printf '%s+%s\n' "${upstream}" "${stamp}"
}
VERSION="$(deb_version "${VERSION}" "${BUILD_STAMP}")"

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
  mkdir -p "${STAGE}/usr/share/applications" "${STAGE}/usr/share/icons/hicolor"
  # shellcheck source=desktop-icons.sh
  source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/desktop-icons.sh"
  if ! install_hicolor_icons "${STAGE}/usr/share/icons/hicolor" "titanium-inspector" \
      "${PAYLOAD_DIR}/app.ico"; then
    echo "error: failed to install titanium-inspector hicolor icons" >&2
    exit 1
  fi
  # Prefer publish-dir copy (CI/smoke stage it); fall back to packaging tree so
  # local -p:Version builds still ship a launcher entry.
  HERE_PKG="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  DESKTOP_IN="${PAYLOAD_DIR}/TitaniumInspector.desktop.in"
  if [[ ! -f "${DESKTOP_IN}" ]]; then
    DESKTOP_IN="${HERE_PKG}/TitaniumInspector.desktop.in"
  fi
  if [[ ! -f "${DESKTOP_IN}" ]]; then
    echo "error: TitaniumInspector.desktop.in missing (payload and ${HERE_PKG})" >&2
    exit 1
  fi
  sed -e "s|@EXEC@|/opt/${NAME}/${EXE}|g" -e "s|@ICON@|titanium-inspector|g" \
    "${DESKTOP_IN}" > "${STAGE}/usr/share/applications/titanium-inspector.desktop"
  # Double-clicking a newer .deb opens App Center, which shows a disabled
  # "Installed" button even when the file version is higher. This handler asks
  # to update Titanium packages and forwards every other package to App Center.
  LIB_DIR="${STAGE}/usr/lib/${NAME}"
  mkdir -p "${LIB_DIR}"
  SCRIPT_SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/open-downloaded-package.sh"
  cp "${SCRIPT_SRC}" "${LIB_DIR}/open-downloaded-package.sh"
  chmod 755 "${LIB_DIR}/open-downloaded-package.sh"
  cat > "${STAGE}/usr/share/applications/titanium-inspector-package.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Titanium package installer
Exec=/usr/lib/${NAME}/open-downloaded-package.sh %f
MimeType=application/vnd.debian.binary-package;application/x-rpm;
NoDisplay=true
Terminal=false
EOF
  # Login cleanup: power loss skips the in-process proxy restore.
  HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  cp "${HERE}/clear-proxy-if-stopped.sh" "${LIB_DIR}/clear-proxy-if-stopped.sh"
  chmod 755 "${LIB_DIR}/clear-proxy-if-stopped.sh"
  mkdir -p "${STAGE}/etc/xdg/autostart" "${STAGE}/usr/lib/systemd/user"
  sed "s|/usr/lib/titanium-inspector/|/usr/lib/${NAME}/|g" \
    "${HERE}/titanium-inspector-clear-proxy.desktop" \
    > "${STAGE}/etc/xdg/autostart/titanium-inspector-clear-proxy.desktop"
  sed "s|/usr/lib/titanium-inspector/|/usr/lib/${NAME}/|g" \
    "${HERE}/titanium-inspector-clear-proxy.service" \
    > "${STAGE}/usr/lib/systemd/user/titanium-inspector-clear-proxy.service"
  cp "${HERE}/titanium-inspector-clear-proxy.timer" \
    "${STAGE}/usr/lib/systemd/user/titanium-inspector-clear-proxy.timer"
fi

DEB_OUT="${OUT_DIR}/${NAME}_${VERSION}_${DEB_ARCH}.deb"
RPM_OUT="${OUT_DIR}/${NAME}-${VERSION}-1.${RPM_ARCH}.rpm"

POSTINST=""
PRERM=""
if [[ "${PRODUCT}" == "inspector" ]]; then
  POSTINST="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/deb-postinst.sh"
  PRERM="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/deb-prerm.sh"
  chmod 755 "${POSTINST}" "${PRERM}"
fi

pkg_dirs=(opt usr)
if [[ -d "${STAGE}/etc" ]]; then
  pkg_dirs+=(etc)
fi

if command -v fpm >/dev/null 2>&1; then
  fpm_post=()
  fpm_pre=()
  if [[ -n "${POSTINST}" ]]; then
    fpm_post=(--after-install "${POSTINST}")
  fi
  if [[ -n "${PRERM}" ]]; then
    fpm_pre=(--before-remove "${PRERM}")
  fi
  fpm -s dir -t deb -n "${NAME}" -v "${VERSION}" -a "${DEB_ARCH}" \
    --description "${DESC}" --license "PolyForm-Noncommercial-1.0.0" \
    --url "https://github.com/justcoding121/titanium-web-proxy" \
    "${fpm_post[@]}" \
    "${fpm_pre[@]}" \
    -C "${STAGE}" -p "${DEB_OUT}" \
    "${pkg_dirs[@]}"
  fpm -s dir -t rpm -n "${NAME}" -v "${VERSION}" -a "${RPM_ARCH}" \
    --description "${DESC}" --license "PolyForm-Noncommercial-1.0.0" \
    --url "https://github.com/justcoding121/titanium-web-proxy" \
    "${fpm_pre[@]}" \
    -C "${STAGE}" -p "${RPM_OUT}" \
    "${pkg_dirs[@]}"
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
Depends: libnuma1, zenity
EOF
  if [[ -n "${POSTINST}" ]]; then
    cp "${POSTINST}" "${DEB_ROOT}/DEBIAN/postinst"
    chmod 755 "${DEB_ROOT}/DEBIAN/postinst"
  fi
  if [[ -n "${PRERM}" ]]; then
    cp "${PRERM}" "${DEB_ROOT}/DEBIAN/prerm"
    chmod 755 "${DEB_ROOT}/DEBIAN/prerm"
  fi
  dpkg-deb --root-owner-group --build "${DEB_ROOT}" "${DEB_OUT}"
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
