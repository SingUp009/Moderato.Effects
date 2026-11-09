#!/usr/bin/env bash
set -euo pipefail

dotnet_command=${1:-dotnet}
artifacts=${2:-artifacts}
commit=${3:-$(git rev-parse HEAD)}
packages=(Moderato.Effects Moderato.Effects.Hosting Moderato.Effects.Http Moderato.Effects.EntityFrameworkCore)
keys=(CORE_VERSION HOSTING_VERSION HTTP_VERSION EF_VERSION)
suffix=''
if [[ "${GITHUB_REF:-}" != refs/tags/* ]]; then
  suffix=${VALIDATION_PACKAGE_SUFFIX:-ci.${GITHUB_RUN_NUMBER:?}.${GITHUB_RUN_ATTEMPT:?}}
fi

versions=()
for package in "${packages[@]}"; do
  versions+=("$("$dotnet_command" msbuild "src/$package" -p:ValidationPackageSuffix="$suffix" -getProperty:PackageVersion)")
done

release_index=''
if [[ "${GITHUB_REF:-}" == refs/tags/* ]]; then
  tag=${GITHUB_REF#refs/tags/}
  for index in "${!packages[@]}"; do
    expected="${packages[$index]}/v${versions[$index]}"
    if [[ "$index" == 0 ]]; then expected="v${versions[$index]}"; fi
    if [[ "$tag" == "$expected" ]]; then release_index=$index; break; fi
  done
  if [[ -z "$release_index" ]]; then
    echo "::error::Tag $tag does not match a package ID and its declared version" >&2
    exit 1
  fi
fi

mkdir -p "$artifacts"
# Only artifacts from this invocation may reach verification or publication.
rm -f "$artifacts"/*.nupkg "$artifacts"/*.snupkg
mkdir -p "$artifacts/release"
rm -f "$artifacts/release"/*.nupkg "$artifacts/release"/*.snupkg
for index in "${!packages[@]}"; do
  printf '%s=%s\n' "${keys[$index]}" "${versions[$index]}"
done > "$artifacts/versions.env"
if [[ -n "$release_index" ]]; then
  printf 'RELEASE_PACKAGE=%s\n' "${packages[$release_index]}" >> "$artifacts/versions.env"
fi

for package in "${packages[@]}"; do
  "$dotnet_command" pack "src/$package" -c Release -p:ValidationPackageSuffix="$suffix" -o "$artifacts"
done
"$dotnet_command" run --project tests/Moderato.Effects.PackageVerifier -c Release -- "$artifacts" "${versions[@]}" "$commit"

if [[ -n "$release_index" ]]; then
  mkdir -p "$artifacts/release"
  package=${packages[$release_index]}
  version=${versions[$release_index]}
  cp "$artifacts/$package.$version.nupkg" "$artifacts/$package.$version.snupkg" "$artifacts/release/"
fi
