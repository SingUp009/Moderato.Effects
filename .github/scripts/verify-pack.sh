#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cat > "$work/dotnet" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
version_for() {
  case "$1" in
    src/Moderato.Effects) echo "${STUB_CORE_VERSION:-1.2.0}" ;;
    src/Moderato.Effects.Hosting) echo 0.9.0 ;;
    src/Moderato.Effects.Http) echo "${STUB_HTTP_VERSION:-0.8.0}" ;;
    src/Moderato.Effects.EntityFrameworkCore) echo 0.7.0 ;;
    *) exit 1 ;;
  esac
}
case "$1" in
  msbuild|pack)
    command=$1
    project=$2
    shift 2
    suffix=''
    output=''
    while (( $# )); do
      case "$1" in
        -p:ValidationPackageSuffix=*) suffix=${1#*=} ;;
        -o) output=$2; shift ;;
      esac
      shift
    done
    version=$(version_for "$project")
    if [[ -n "$suffix" ]]; then version="$version-$suffix"; fi
    if [[ "$command" == msbuild ]]; then echo "$version"; exit; fi
    touch "$output/${project#src/}.$version.nupkg" "$output/${project#src/}.$version.snupkg"
    ;;
  run)
    while [[ "$1" != -- ]]; do shift; done
    shift
    [[ $# == 6 ]]
    printf '%s\n' "$@" > "$1/verifier-arguments.txt"
    exit "${STUB_VERIFY_FAIL:-0}"
    ;;
  *) exit 1 ;;
esac
STUB
chmod +x "$work/dotnet"

run_pack() {
  GITHUB_REF="$1" GITHUB_RUN_NUMBER=7 GITHUB_RUN_ATTEMPT=2 \
    bash .github/scripts/pack.sh "$work/dotnet" "$work/$2" test-commit
}

run_pack refs/heads/main ci
for item in CORE_VERSION=1.2.0 HOSTING_VERSION=0.9.0 HTTP_VERSION=0.8.0 EF_VERSION=0.7.0; do
  grep -qx "$item-ci.7.2" "$work/ci/versions.env"
done
[[ -z $(find "$work/ci/release" -type f -print -quit) ]]
! grep -q '^RELEASE_PACKAGE=' "$work/ci/versions.env"

for item in 'v1.2.0 Moderato.Effects 1.2.0' \
  'Moderato.Effects.Hosting/v0.9.0 Moderato.Effects.Hosting 0.9.0' \
  'Moderato.Effects.Http/v0.8.0 Moderato.Effects.Http 0.8.0' \
  'Moderato.Effects.EntityFrameworkCore/v0.7.0 Moderato.Effects.EntityFrameworkCore 0.7.0'; do
  read -r tag package version <<< "$item"
  run_pack "refs/tags/$tag" release
  grep -qx "RELEASE_PACKAGE=$package" "$work/release/versions.env"
  [[ -f "$work/release/release/$package.$version.nupkg" ]]
  [[ -f "$work/release/release/$package.$version.snupkg" ]]
  [[ $(find "$work/release/release" -type f | wc -l) == 2 ]]
  grep -qx CORE_VERSION=1.2.0 "$work/release/versions.env"
  grep -qx HOSTING_VERSION=0.9.0 "$work/release/versions.env"
done

STUB_CORE_VERSION=1.3.0 run_pack refs/tags/v1.3.0 core-update
[[ -f "$work/core-update/release/Moderato.Effects.1.3.0.nupkg" ]]
grep -qx HTTP_VERSION=0.8.0 "$work/core-update/versions.env"

STUB_HTTP_VERSION=0.8.1 run_pack refs/tags/Moderato.Effects.Http/v0.8.1 http-update
[[ -f "$work/http-update/release/Moderato.Effects.Http.0.8.1.nupkg" ]]
grep -qx CORE_VERSION=1.2.0 "$work/http-update/versions.env"
STUB_CORE_VERSION=1.2.0-preview.4 run_pack refs/heads/main prerelease
grep -qx CORE_VERSION=1.2.0-preview.4-ci.7.2 "$work/prerelease/versions.env"

for tag in v0.9.0 Moderato.Effects.Http/v0.9.0 Unknown/v1.2.0; do
  if run_pack "refs/tags/$tag" invalid > "$work/invalid-tag.log" 2>&1; then echo "Unexpected acceptance: $tag" >&2; exit 1; fi
  grep -q 'does not match a package ID and its declared version' "$work/invalid-tag.log"
  [[ ! -e "$work/invalid" ]]
done

if STUB_VERIFY_FAIL=1 run_pack refs/tags/v1.2.0 failed; then
  echo 'Verifier failure was ignored' >&2
  exit 1
fi
[[ -z $(find "$work/failed/release" -type f -print -quit) ]]
sdk=${1:-dotnet}
cat > "$work/suffix.csproj" <<PROJECT
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <Version>1.2.3-preview.4</Version>
  </PropertyGroup>
  <Import Project="$root/Directory.Build.targets" />
</Project>
PROJECT
actual=$("$sdk" msbuild "$work/suffix.csproj" -p:ValidationPackageSuffix=ci.9.9 -getProperty:PackageVersion)
[[ "$actual" == 1.2.3-preview.4-ci.9.9 ]]
actual=$("$sdk" msbuild "$work/suffix.csproj" -getProperty:PackageVersion)
[[ "$actual" == 1.2.3-preview.4 ]]
echo 'PASS: independent versions, release selection, failure propagation and real SDK suffix evaluation.'
