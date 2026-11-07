# Moderato.Effects

Effect System for C# on .NET 11 / C# 15 unions. Builds on Moderato.Results.

## Docs

- `docs/` is not in version control. Design notes (D1–D18), phase records and sibling-library requirements are kept locally, excluded through `.git/info/exclude` rather than `.gitignore`. If `docs/design.md` is present, read it before changing behavior and ask before deviating from it; if it is absent, ask rather than inferring a contract from the code alone.
- The contracts themselves live in the code: `PublicAPI.Shipped.txt` fixes the public surface, and `why` comments in the source carry the reasons behind non-obvious behavior.
- The user guide (English) lives in the GitHub wiki, a separate repository. Keep it in step with the API; the README only links to it.

## Code conventions

- Do not write comments in source code, except to explain a *why* the code cannot show. Write those in English.
- XML doc comments: short and to the point. A one-line `<summary>` of what the member does, plus `<param>` and `<returns>` where they add information.
- Naming: private fields `camelCase` (no `_` or `s_` prefix, static or not), constants `UPPER_SNAKE_CASE`, enum members `PascalCase`. `.editorconfig` enforces the first two; its naming rules cannot target enum members.
- Match Moderato.Results: `ArgumentNullException.ThrowIfNull` on delegates, `default` structs are invalid, `ConfigureAwait(false)` inside the library.

## Test conventions

Tests are first-class documentation.

- Cover the happy path, edge cases (if any), and error paths of each behavior.
- Use Given-When-Then, marked with `// Given`, `// When`, `// Then`.
- Write the When step exactly as a user would call the API. Do not call internals or test helpers there.
- Name tests `<Method>_<Condition>_<ExpectedResult>()`.

## Build

```
dotnet test -c Release
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
dotnet run --project samples/Moderato.Effects.Samples
dotnet run -c Release --project bench/Moderato.Effects.Benchmarks -- --filter '*' --inProcess
```

`tests/Moderato.Effects.PackageTests` is not in the solution: CI runs it against the packed `.nupkg` (see the `package` job in `.github/workflows/ci.yml`). To run it locally, `dotnet pack src/Moderato.Effects -c Release -o artifacts` first and pass `-p:ModeratoEffectsVersion=<Version>`.

`src/Moderato.Effects/PublicAPI.Unshipped.txt` tracks the public API; add new members there (RS0016). Release steps are in `RELEASING.md`.
## Commit messages

```
<optional emoji> [optional issue-number] <type>(scope): <description>

[optional body]

[optional footer(s)]
```

`✨ #12 feat(auth): add login feature`

- Type, scope and description are required. Issue number is strongly recommended — omit it only when there is no issue, or for a hotfix. Emoji, body and footer are optional.
- Type: `fix` (user-facing bug, SemVer PATCH), `feat` (user-facing feature, SemVer MINOR), `build`, `ci`, `docs`, `perf`, `refactor`, `test`, `style`, `chore`.
- Scope names the changed capability in one word. **If one word cannot name it, the commit does too much and should be split.**
- Breaking changes carry `BREAKING CHANGE:` in the footer, or `!` right after the type/scope: `💥 #32 feat(db)!: allow provided config object to extend other configs`.
- Emoji come from [gitmoji](https://gitmoji.dev/).
