#!/usr/bin/env bash
set -euo pipefail

# Pass the executable command, e.g. dotnet path/to/Moderato.Effects.SoakProbe.dll.
timeout 30s "$@" self-check
for scenario in gen resource provide bind cancel fiber die host; do
  timeout 30s "$@" "$scenario" 0.02 0.3
done
for value in NaN Infinity invalid 0 -1 1441; do
  set +e
  timeout 10s "$@" gen "$value" 1
  status=$?
  set -e
  if (( status != 2 )); then echo "FAIL invalid duration: $value exit=$status" >&2; exit 1; fi
done
