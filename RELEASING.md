# Releasing

A release is a version-bump PR followed by a tag. CI publishes the package that the tagged build tested.

## 1. Open the release PR

On a branch from `main` (for example `chore/release-0.1.0-preview.1`):

- [ ] **Version**: set `<Version>` in `src/Moderato.Effects/Moderato.Effects.csproj`. CI rejects a tag that does not match it.
- [ ] **Public API**: move every line of `src/Moderato.Effects/PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`.
  - Add the plain entries to Shipped.
  - For each `*REMOVED*` entry, delete the matching line from Shipped.
  - Keep Shipped sorted, and leave Unshipped with only `#nullable enable`.
- [ ] **Analyzer releases**: move the rules in `src/Moderato.Effects.SourceGenerator/AnalyzerReleases.Unshipped.md` into `AnalyzerReleases.Shipped.md` under a `## Release <Version>` heading.
- [ ] **Changelog**: in `CHANGELOG.md`, rename `[Unreleased]` to the new version with today's date, add an empty `[Unreleased]` above it, and update the compare links at the bottom. Breaking changes come first, and a source-compatible but binary-breaking change is still a breaking change.
- [ ] `dotnet test -c Release` passes.

Merge the PR once CI is green.

## 2. Tag and publish

- [ ] On GitHub, **Releases → Draft a new release**.
  - **Tag:** `v<Version>`, created on `main`.
  - **Title:** the same as the tag.
  - **Notes:** the version's changelog section.
  - Mark previews as **pre-release**.
- [ ] Publishing the release starts CI. `lint`, `format` and `test` must pass; then `pack` checks the tag against `<Version>`, packs and uploads the packages.
- [ ] `publish` waits for approval in the `nuget` environment. Approve it under **Actions → the run → Review deployments**.
- [ ] Confirm the version on https://www.nuget.org/packages/Moderato.Effects. Indexing can take 5 to 15 minutes.

A version on nuget.org can be unlisted but never deleted or reused, so check the tag before publishing.
