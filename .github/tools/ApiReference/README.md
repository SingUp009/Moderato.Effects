# Wiki API reference

Generate the reference from the four packages' DLLs and XML documentation:

```sh
dotnet run --project .github/tools/ApiReference -c Release -- artifacts/api-reference
python3 .github/scripts/verify-api-reference.py artifacts/api-reference
```

Use the SDK in `global.json`. The tool references all four projects and uses Roslyn's symbol display rather than maintaining a separate C# signature formatter. It uses the same Roslyn version as the source generator. Documentation generation does not change the packages or publish the Wiki.

Type-page filenames preserve C# casing and omit the common `Moderato.Effects.` namespace prefix, such as `Http.EffectRequest.md`. Generic parameter names use square brackets, such as `Effect[T, E, R].md`; extensionless link targets are URI-encoded. Package index names and the namespaces shown in the page bodies remain unchanged. `Home.md` records the assembly informational versions, including the source commit. XML documentation supplies descriptions, type parameters, parameters, returns, remarks and exceptions. References to types in the four packages link to their generated pages. Inherited members are documented on their declaring type; compiler-generated methods that cannot be called by name are omitted.

The output directory must be empty so obsolete files cannot remain in the result. Review the generated Markdown before publishing it to the separate Wiki repository's default branch. Guides are maintained in `document/src/content/docs/`; do not recreate the removed guide relocation pages.
