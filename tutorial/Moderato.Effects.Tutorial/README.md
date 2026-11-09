# Moderato.Effects Tutorial

Executable chapters for the Moderato.Effects tutorial. Use the .NET SDK pinned in the repository's `global.json`.

Run a chapter from the repository root:

```sh
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 01
dotnet run --project tutorial/Moderato.Effects.Tutorial -- 02
```

| Chapter | Source | Learn |
|---|---|---|
| 01 | [Results](Chapters/01_Results.cs) | Return and handle success and expected failure with `Result<T, E>` |
| 02 | [Your first effect](Chapters/02_FirstEffect.cs) | Describe work with `Effect.Gen`, run it and read its result |

Run without a chapter, or pass `--help`, to list the chapters. Unknown chapters and extra arguments return a nonzero exit code.

The lesson pages are in `document/src/content/docs/tutorial/`. Each chapter runs independently.

To check the expected chapter output and argument handling:

```sh
dotnet build tutorial/Moderato.Effects.Tutorial -c Release
bash tutorial/verify.sh dotnet tutorial/Moderato.Effects.Tutorial/bin/Release/net11.0/Moderato.Effects.Tutorial.dll
```
