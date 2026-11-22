#!/bin/sh
# The musl CLI is self-contained and aborts in the .NET host, before Main, when
# Alpine has no ICU. Put a shell in front of the apphost so that case names the
# package to install. Hosts that already have ICU exec the real binary.
set -eu
out=${1:?publish directory}
for name in titanium twp; do
  src="$out/$name"
  bin="$out/$name.bin"
  if [ ! -f "$bin" ]; then
    if [ ! -f "$src" ]; then
      echo "error: $src is missing" >&2
      exit 1
    fi
    mv "$src" "$bin"
  fi
  cat > "$src" <<EOF
#!/bin/sh
target=\$0
while [ -L "\$target" ]; do
  link=\$(readlink "\$target") || exit 1
  case \$link in
    /*) target=\$link ;;
    *) target=\$(dirname "\$target")/\$link ;;
  esac
done
dir=\$(CDPATH= cd -- "\$(dirname "\$target")" && pwd)
invariant=\$(printf '%s' "\${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-}" | tr '[:upper:]' '[:lower:]')
if [ "\$invariant" = 1 ] || [ "\$invariant" = true ]; then
  exec "\$dir/$name.bin" "\$@"
fi
icu_present() {
  for p in /usr/lib/libicuuc.so* /lib/libicuuc.so* /usr/lib/*/libicuuc.so*; do
    if [ -e "\$p" ]; then
      return 0
    fi
  done
  return 1
}
probe=\${TITANIUM_ICU_PROBE:-auto}
if [ "\$probe" = missing ] || { [ "\$probe" = auto ] && ! icu_present; }; then
  echo "Titanium needs ICU. On Alpine install it with: apk add icu-libs" >&2
  exit 1
fi
exec "\$dir/$name.bin" "\$@"
EOF
  chmod +x "$src" "$bin"
done
