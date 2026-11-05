# Releasing

A release is a version-bump PR followed by a tag. CI publishes the package that the tagged build tested.

## 1. Open the release PR

On a branch from `main` (for example `chore/release-0.1.0-preview.1`):

- [ ] **Version**: set `<Version>` in `src/Moderato.Effects/Moderato.Effects.csproj`, `src/Moderato.Effects.Hosting/Moderato.Effects.Hosting.csproj` and `src/Moderato.Effects.Http/Moderato.Effects.Http.csproj`. CI rejects mismatched versions or tags. Development versions are placeholders: never reuse an already published version for changed binaries.
- [ ] **New package IDs**: verify that the nuget.org [Trusted Publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing#select-scopes) permits new packages and its glob covers every intended ID. `Moderato.Effects*` covers Core, Hosting and HTTP; `Moderato.Effects.*` omits Core. Keep the repository, workflow (`ci.yml`) and environment (`nuget`) restrictions.
- [ ] **Public API**: move every line of `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` in Core, Hosting and HTTP.
  - Add the plain entries to Shipped.
  - For each `*REMOVED*` entry, delete the matching line from Shipped.
  - Keep Shipped sorted, and leave Unshipped with only `#nullable enable`.
- [ ] **Analyzer releases**: move the rules in `src/Moderato.Effects.SourceGenerator/AnalyzerReleases.Unshipped.md` into `AnalyzerReleases.Shipped.md` under a `## Release <Version>` heading.
  - The heading takes a `System.Version`, so a preview uses a fourth number: `0.1.0-preview.1` is `## Release 0.1.0.1`, and `0.1.0` is `## Release 0.1.0`. Do not suppress RS2007: a heading it cannot read leaves that release's rules unchecked.
- [ ] **Changelog**: in `CHANGELOG.md`, rename `[Unreleased]` to the new version with today's date, add an empty `[Unreleased]` above it, and update the compare links at the bottom. Breaking changes come first, and a source-compatible but binary-breaking change is still a breaking change.
  - `Tests` is an Unreleased-only section for verification changes with no package-observable effect; remove it before renaming `[Unreleased]`. API, behavior, and allocation changes belong under the normal change types.
- [ ] `dotnet test -c Release` passes.
- [ ] All samples run, including `samples/Moderato.Effects.Http.Samples -c Release -- --verify` for real HTTP/OpenAPI checks.
- [ ] The Core, Hosting and HTTP package consumers pass under JIT and Native AOT; the Hosting-only and HTTP-only consumers receive the transitive generators and analyzers.
- [ ] `tests/Moderato.Effects.PackageVerifier` confirms the expected metadata, XML docs, README, symbol packages and SourceLink commit for all three packages.
- [ ] Review `docs/http-validation.md`, HTTP direct/adapter timing and allocation evidence, and the RC3/GA validation boundary.
- [ ] Update the English wiki, including Hosting, HTTP and any Core operator changes. Mark unreleased APIs until publication.

Merge the PR once CI is green.

## 2. Tag and publish

- [ ] On GitHub, **Releases → Draft a new release**.
  - **Tag:** `v<Version>`, created on `main`.
  - **Title:** the same as the tag.
  - **Notes:** the version's changelog section.
  - Mark previews as **pre-release**.
- [ ] Publishing the release starts CI. `lint`, `format` and `test` must pass; then `pack` checks the tag against `<Version>`, packs and uploads the packages, and `package` runs a consumer of them, checks that their analyzers run, and publishes it with Native AOT.
- [ ] `publish` waits for approval in the `nuget` environment. Approve it under **Actions → the run → Review deployments**.
- [ ] Confirm the version on https://www.nuget.org/packages/Moderato.Effects, https://www.nuget.org/packages/Moderato.Effects.Hosting and https://www.nuget.org/packages/Moderato.Effects.Http. Indexing can take 5 to 15 minutes.

A version on nuget.org can be unlisted but never deleted or reused, so check the tag before publishing.

## Local verification without publishing

Use a unique unpublished version and the project-local package caches. The three packages use minimum-version dependencies, so upgrading Core or Microsoft.Extensions remains possible; their compatible higher versions are not pinned away.

```bash
validation_version="0.1.0-local.$(git rev-parse --short HEAD).1"
dotnet pack src/Moderato.Effects -c Release -p:Version="$validation_version" -o artifacts
dotnet pack src/Moderato.Effects.Hosting -c Release -p:Version="$validation_version" -o artifacts
dotnet pack src/Moderato.Effects.Http -c Release -p:Version="$validation_version" -o artifacts
dotnet run --project tests/Moderato.Effects.PackageVerifier -c Release -- artifacts "$validation_version" "$(git rev-parse HEAD)"
dotnet run --project tests/Moderato.Effects.PackageTests -c Release -p:ModeratoEffectsVersion="$validation_version"
dotnet run --project tests/Moderato.Effects.Hosting.PackageTests -c Release -p:ModeratoEffectsVersion="$validation_version"
dotnet run --project tests/Moderato.Effects.Http.PackageTests -c Release -p:ModeratoEffectsVersion="$validation_version"
dotnet publish tests/Moderato.Effects.Hosting.PackageTests -c Release -r linux-x64 -p:ModeratoEffectsVersion="$validation_version" -o artifacts/aot-hosting
./artifacts/aot-hosting/Moderato.Effects.Hosting.PackageTests
dotnet publish tests/Moderato.Effects.Http.PackageTests -c Release -r linux-x64 -p:ModeratoEffectsVersion="$validation_version" -o artifacts/aot-http
./artifacts/aot-http/Moderato.Effects.Http.PackageTests
```

Increment the suffix after changing package contents. Non-tag CI builds use a unique `-ci.<run>.<attempt>` version stored in `artifacts/version.txt`; only tag builds can reach the existing approval-gated publishing job. Use the SDK in `global.json`; RC3 replaces the vulnerable RC2 Native AOT toolchain without suppressing NuGet security checks.
