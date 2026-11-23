#!/usr/bin/env bash
# Compatibility wrapper. Prefer bump-formula-from-sums.sh.
exec "$(cd "$(dirname "$0")" && pwd)/bump-formula-from-sums.sh" "$@"
