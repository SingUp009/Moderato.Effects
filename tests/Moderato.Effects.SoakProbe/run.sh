#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 3 || ! $2 =~ ^[0-9]{1,4}$ ]] || (( 10#$2 < 1 || 10#$2 > 1440 )); then
  echo 'Usage: bash run.sh gen|resource|provide|bind|cancel|fiber|die|host <integer minutes: 1..1440> <new output directory>' >&2
  exit 2
fi
case $1 in gen|resource|provide|bind|cancel|fiber|die|host) ;; *) exit 2 ;; esac
scenario=$1
minutes=$((10#$2))
mkdir -- "$3"
output=$(cd -- "$3" && pwd)
cd "$(dirname "$0")/../.."
timeout --kill-after=10s 300s dotnet build tests/Moderato.Effects.SoakProbe -c Release --nologo > "$output/build.log" 2>&1
binary=tests/Moderato.Effects.SoakProbe/bin/Release/net11.0/Moderato.Effects.SoakProbe.dll
{
  date -u +%Y-%m-%dT%H:%M:%SZ
  git rev-parse HEAD
  git status --short
  dotnet --info
  sha256sum tests/Moderato.Effects.SoakProbe/*.cs tests/Moderato.Effects.SoakProbe/*.csproj tests/Moderato.Effects.SoakProbe/*.sh
  sha256sum tests/Moderato.Effects.SoakProbe/bin/Release/net11.0/*.dll tests/Moderato.Effects.SoakProbe/bin/Release/net11.0/*.runtimeconfig.json
} > "$output/manifest.txt"
set +e
timeout --kill-after=10s "$((minutes * 60 + 300))s" dotnet "$binary" "$scenario" "$minutes" 10 > "$output/run.log" 2>&1
status=$?
set -e
printf 'exit=%s finished=%s\n' "$status" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$output/manifest.txt"
if (( status == 124 || status == 137 )); then echo 'FAIL watchdog: execution exceeded its time allowance' >&2; fi
cat "$output/run.log"
exit "$status"
