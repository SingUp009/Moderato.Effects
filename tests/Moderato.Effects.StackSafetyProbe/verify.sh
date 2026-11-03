#!/usr/bin/env bash
set -euo pipefail
ulimit -c 0

log=$(mktemp)
trap 'rm -f "$log"' EXIT

for mode in sync pending; do
  for scenario in map bind recursive-bind provide query recover conversions resource resource-map resource-query recursive-resource resource-conversions scope retention; do
    if ! timeout 60s "$@" "$scenario" 100000 "$mode" > "$log" 2>&1; then
      head -c 6000 "$log"
      exit 1
    fi
    cat "$log"
  done
done

# Control wrappers still recurse; keep their finite-depth coverage separate from the iterative core.
for mode in sync pending; do
  for scenario in retry-nest timeout-nest; do
    if ! timeout 60s "$@" "$scenario" 2048 "$mode" > "$log" 2>&1; then
      head -c 6000 "$log"
      exit 1
    fi
    cat "$log"
  done
done
