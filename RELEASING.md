# Releasing

Each package has its own version and release tag. A release is a version-bump PR followed by a tag; CI publishes only the tagged package after testing the complete package set.

## 1. Open the release PR

On a branch from `main` (for example `chore/release-0.1.0-preview.1`):

- [ ] **Version**: update `<Version>` only in the projects being released. An extension can keep its version when only Core changes, provided the new Core remains compatible. Project references supply the current Core package version as the extension's minimum dependency when it is packed. If an extension needs newer Core APIs, release that Core version first. Never reuse a published version for changed binaries or dependency metadata.
- [ ] **New package IDs**: verify that the nuget.org [Trusted Publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing#select-scopes) permits new packages and its glob covers every intended ID. `Moderato.Effects*` covers Core, Hosting, HTTP and EF; `Moderato.Effects.*` omits Core. Keep the repository, workflow (`ci.yml`) and environment (`nuget`) restrictions.
- [ ] **Public API**: move the released APIs from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` in the packages being released.
  - Add the plain entries to Shipped.
  - For each `*REMOVED*` entry, delete the matching line from Shipped.
  - Keep Shipped sorted, and leave Unshipped with only `#nullable enable`.
- [ ] **Analyzer releases**: move the rules in `src/Moderato.Effects.SourceGenerator/AnalyzerReleases.Unshipped.md` into `AnalyzerReleases.Shipped.md` under a `## Release <Version>` heading.
  - The heading takes a `System.Version`, so a preview uses a fourth number: `0.1.0-preview.1` is `## Release 0.1.0.1`, and `0.1.0` is `## Release 0.1.0`. Do not suppress RS2007: a heading it cannot read leaves that release's rules unchecked.
- [ ] **Changelog**: in `CHANGELOG.md`, rename `[Unreleased]` to the new version with today's date, add an empty `[Unreleased]` above it, and update the compare links at the bottom. Breaking changes come first, and a source-compatible but binary-breaking change is still a breaking change.
  - `Tests` is an Unreleased-only section for verification changes with no package-observable effect; remove it before renaming `[Unreleased]`. API, behavior, and allocation changes belong under the normal change types.
- [ ] **Release notes**: prepare a separate body following the previous GitHub Release's heading, applicable change sections and PR-linked bullets. Include this version's validation limits; review the text before publishing.
- [ ] `dotnet test -c Release` passes.
- [ ] All samples run. `dotnet run --project tests/Moderato.Effects.Http.SampleTests -c Release` passes the real HTTP/OpenAPI checks.
- [ ] The Core, Hosting and HTTP package consumers pass under JIT and Native AOT; the EF consumer passes under JIT. EF Native AOT is not validated. All integration-only consumers receive the transitive generators and analyzers.
- [ ] `tests/Moderato.Effects.PackageVerifier` confirms the expected metadata, XML docs, README, symbol packages and SourceLink commit for all four packages.
- [ ] Review the local HTTP validation notes, HTTP direct/adapter timing and allocation evidence, and the RC3/GA validation boundary.
- [ ] Update the English guides in `document/src/content/docs/` for public API and behavior changes. Generate and review the separate Wiki's API reference using [.github/tools/ApiReference](.github/tools/ApiReference/README.md).

Merge the PR once CI is green.

## 2. Tag and publish

- [ ] On GitHub, **Releases → Draft a new release**.
  - **Tag**, created on `main`:
    - Core: `v<Version>` (for example, `v1.2.0`).
    - Hosting: `Moderato.Effects.Hosting/v<Version>`.
    - HTTP: `Moderato.Effects.Http/v<Version>`.
    - EF: `Moderato.Effects.EntityFrameworkCore/v<Version>`.
    - Use one tag for each package being released, with its own version. Release a required Core version before an extension that depends on it.
  - **Title:** the same as the tag.
  - **Notes:** the separately prepared and reviewed release notes.
  - Mark previews as **pre-release**.
- [ ] Publishing the release starts CI. `lint`, `format` and `test` must pass; then `pack` checks the tag against the selected package's own version, packs all four packages at their declared versions and uploads them for validation. `package` runs consumers using each package's version, checks their analyzers and exercises supported consumers with Native AOT. EF currently has JIT checks only. Only the tagged package and its symbols are copied to `artifacts/release/` for publication.
- [ ] `publish` waits for approval in the `nuget` environment. Approve it under **Actions → the run → Review deployments**. Before an extension is pushed, CI confirms that its required Core version is already available on nuget.org; wait for indexing and rerun if it is not.
- [ ] Confirm the selected package version on nuget.org. Packages not selected by this tag must remain unchanged. Indexing can take 5 to 15 minutes.
- [ ] After the packages are publicly available, regenerate and review the Wiki API reference from the released source. Publish the reviewed reference to the separate Wiki repository. Verify the documentation site's installation commands, examples and version references against the released packages.

A version on nuget.org can be unlisted but never deleted or reused, so check the tag before publishing.

## Local verification without publishing

Use a unique validation suffix and the project-local package caches. The suffix is applied to each project's own version, including referenced Core, without overriding other projects' versions. Packages use minimum-version dependencies; compatible higher Core and Microsoft.Extensions versions remain usable.

From the repository root, using .NET 11 or later and the SDK pinned in `global.json`:

```sh
VALIDATION_PACKAGE_SUFFIX="local.$(git rev-parse --short HEAD).1" \
  bash .github/scripts/pack.sh dotnet artifacts "$(git rev-parse HEAD)"
source artifacts/versions.env
dotnet run --project tests/Moderato.Effects.PackageTests -c Release -p:ModeratoEffectsVersion="$CORE_VERSION"
dotnet run --project tests/Moderato.Effects.Hosting.PackageTests -c Release -p:ModeratoEffectsVersion="$HOSTING_VERSION"
dotnet run --project tests/Moderato.Effects.Http.PackageTests -c Release -p:ModeratoEffectsVersion="$HTTP_VERSION"
dotnet run --project tests/Moderato.Effects.EntityFrameworkCore.PackageTests -c Release -p:ModeratoEffectsVersion="$EF_VERSION"
dotnet publish tests/Moderato.Effects.Hosting.PackageTests -c Release -r linux-x64 -p:ModeratoEffectsVersion="$HOSTING_VERSION" -o artifacts/aot-hosting
./artifacts/aot-hosting/Moderato.Effects.Hosting.PackageTests
dotnet publish tests/Moderato.Effects.Http.PackageTests -c Release -r linux-x64 -p:ModeratoEffectsVersion="$HTTP_VERSION" -o artifacts/aot-http
./artifacts/aot-http/Moderato.Effects.Http.PackageTests
```

Increment the suffix after changing package contents. Non-tag CI builds append `-ci.<run>.<attempt>` to each declared version. `artifacts/versions.env` records the four resolved versions for downstream consumers. Tag builds keep the declared versions and stage only the selected package; non-tag builds stage nothing for publication. The existing `nuget` environment approval remains required.

`bash .github/scripts/verify-pack.sh` checks version selection with differing package versions, Core-only and extension-only updates, tag rejection and failed verification. The package verifier checks each archive's own version and validates extension dependencies against the packed Core version, not the extension version. The pack script replaces old `.nupkg` and `.snupkg` outputs in its artifact directory; keep hand-saved packages elsewhere.
