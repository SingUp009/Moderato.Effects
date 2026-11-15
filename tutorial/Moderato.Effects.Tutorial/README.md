# Moderato.Effects Tutorial

Executable chapters for the Moderato.Effects tutorial. Use the .NET SDK pinned in the repository's `global.json`.

Run a chapter from the repository root:

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 01
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 02
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 03
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 04
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 05
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 06
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 07
```

| Chapter | Source | Learn |
|---|---|---|
| 01 | [Results](Chapters/01_Results.cs) | Return and handle success and expected failure with `Result<T, E>` |
| 02 | [Your first effect](Chapters/02_FirstEffect.cs) | Describe work with `Effect.Gen`, run it and read its result |
| 03 | [Handling expected errors](Chapters/03_ExpectedErrors.cs) | Read a `Result` inside `Gen` and let an expected failure stop the block |
| 04 | [Composing effects](Chapters/04_ComposingEffects.cs) | Run effects in sequence and pass a successful value to the next step |
| 05 | [Managing dependencies](Chapters/05_ManagingDependencies.cs) | Supply a dependency through the environment and read it with `scope.Env` |
| 06 | [Managing resources](Chapters/06_ManagingResources.cs) | Acquire a resource in a scope and release it on success or failure |
| 07 | [Running effects concurrently](Chapters/07_ConcurrentEffects.cs) | Run independent effects together and observe cancellation and cleanup |

Run without a chapter, or pass `--help`, to list the chapters. Unknown chapters and extra arguments return a nonzero exit code.

The lesson pages are in `document/src/content/docs/tutorial/`. Each chapter runs independently.

To check the expected chapter output and argument handling:

```sh
dotnet build tutorial/Moderato.Effects.Tutorial -c Release
bash tutorial/verify.sh dotnet tutorial/Moderato.Effects.Tutorial/bin/Release/net11.0/Moderato.Effects.Tutorial.dll
```
