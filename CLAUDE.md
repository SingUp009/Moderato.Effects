# Moderato.Effects

Effect System for C# on .NET 11 / C# 15 unions. Builds on Moderato.Results.

## Docs

- `docs/design.md`: design decisions (D1–D18). Read before changing behavior; ask before deviating from it.
- `docs/implementation-plan.md`: phases (P0–P8). Stop after each phase for review.
- `docs/requirements/`: requirements for sibling libraries.
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
dotnet run -c Release --project bench/Moderato.Effects.Benchmarks -- --filter '*'
```

`src/Moderato.Effects/PublicAPI.Unshipped.txt` tracks the public API; add new members there (RS0016). Release steps are in `RELEASING.md`.
