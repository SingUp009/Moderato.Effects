#!/usr/bin/env bash
set -euo pipefail

# Pass the executable command, e.g. dotnet path/to/Moderato.Effects.SoakProbe.dll.
probe=("$@")
timeout 30s "$@" self-check
for scenario in gen resource provide bind cancel fiber die host; do
  timeout 30s "$@" "$scenario" 0.02 0.3
done

expect_argument_error() {
  local status
  set +e
  timeout 10s "${probe[@]}" "$@"
  status=$?
  set -e
  if (( status != 2 )); then echo "FAIL invalid arguments: $* exit=$status" >&2; exit 1; fi
}
for value in NaN Infinity invalid 0 -1 1441; do
  expect_argument_error gen "$value" 1
done
for value in NaN Infinity invalid 0 -1 3601; do
  expect_argument_error gen 1 "$value"
done
expect_argument_error unknown 1 1

server_log=$(mktemp)
trap 'rm -f "$server_log"' EXIT
env DOTNET_gcServer=1 timeout 30s "$@" host 0.02 0.3 > "$server_log" 2>&1 || { cat "$server_log"; exit 1; }
cat "$server_log"
grep -q 'gcServer=True' "$server_log"

"${probe[@]}" verify-signals "${probe[@]}"
