# Moderato.Effects

Effect System for C# on .NET 11 / C# 15 unions. Builds on Moderato.Results.

## Docs

- `docs/design.md`: design decisions (D1–D18). Read before changing behavior; ask before deviating from it.
- `docs/implementation-plan.md`: phases (P0–P8). Stop after each phase for review.
- `docs/requirements/`: requirements for sibling libraries.

## Code conventions

- Do not write comments in source code, except to explain a *why* the code cannot show. Write those in English.
- XML doc comments: short and to the point. A one-line `<summary>` of what the member does, plus `<param>` and `<returns>` where they add information.
- Match Moderato.Results: naming, `ArgumentNullException.ThrowIfNull` on delegates, `default` structs are invalid, `ConfigureAwait(false)` inside the library.

## Build

```
dotnet test -c Release
```
