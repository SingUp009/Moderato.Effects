# Contributing

Bug reports, documentation improvements and pull requests are welcome. Using the library in applications and sharing what works or is confusing also helps. Please follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## Issues

Search [existing issues](https://github.com/SingUp009/Moderato.Effects/issues) before opening a new one.

- **Bug reports:** include the affected packages and their versions, your SDK and runtime environment, a small reproduction, and the expected and actual behavior. For cancellation or concurrency bugs, include the order of events and how often the problem occurs.
- **Feature requests:** explain the problem you want to solve. A short example of how you would like to use the API helps; you do not need to have an implementation in mind.
- **Questions and documentation:** a blank issue is fine. Include the code or documentation page you are asking about.

For usage examples, see the [documentation](https://moderato-effects.009singup.workers.dev) and [samples](samples/). Issues specific to `Moderato.Results` belong in [that repository](https://github.com/SingUp009/Moderato.Results/issues).

## Working on a change

Fork the repository and create a branch from `main`. Keep each pull request focused on one change. Small fixes can go directly to a pull request. Discuss public API changes and changes to cancellation, resource ownership or failure precedence in an issue before implementing them.

### Library changes

Install the .NET SDK selected by [global.json](global.json), including preview support when required. The projects target .NET 11 and use C# 15 unions. Run these commands from the repository root:

```sh
dotnet build -c Release
dotnet test -c Release
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
```

Compiler and analyzer warnings are treated as errors. Run the relevant sample when changing its behavior; for example:

```sh
dotnet run -c Release --project samples/Moderato.Effects.Samples
```

[CI](.github/workflows/ci.yml) also checks packed consumers, analyzers and supported Native AOT scenarios. Package tests are separate from the solution tests; see [RELEASING.md](RELEASING.md#local-verification-without-publishing) for local package verification. For performance changes, include the benchmark command, runtime, hardware and before/after results. Full benchmarks are not needed for documentation-only changes.

### Documentation and tutorial changes

The website source is in [document/src/content/docs](document/src/content/docs/). Use Node.js 26, matching the site's tested build environment. To preview edits with hot reload:

```sh
cd document
npm ci
npm run dev
```

Run `npm run build` from `document/` before submitting website changes. Documentation-only changes do not require the .NET checks or a Cloudflare account.

For changes to the executable tutorial chapters, also build and verify them from the repository root:

```sh
dotnet build tutorial/Moderato.Effects.Tutorial -c Release
bash tutorial/verify.sh dotnet tutorial/Moderato.Effects.Tutorial/bin/Release/net11.0/Moderato.Effects.Tutorial.dll
```

## Code and tests

Follow the surrounding code and [.editorconfig](.editorconfig):

- Private fields use `camelCase` without a prefix, constants use `UPPER_SNAKE_CASE`, and enum members use `PascalCase`.
- Validate delegate arguments with `ArgumentNullException.ThrowIfNull`. Treat default effect and resource structs as invalid, and use `ConfigureAwait(false)` inside the library.
- Add source comments only when they explain a reason the code cannot show. Write them in English. Keep public XML documentation short.
- Test the happy path, relevant edge cases and error paths. Name tests `<Method>_<Condition>_<ExpectedResult>` and mark the steps with `// Given`, `// When` and `// Then`. The When step should call the public API as a user would.
- For concurrency and cancellation tests, coordinate events explicitly rather than relying on delays to establish their order.
- Keep samples small and runnable. They demonstrate how to use a feature; put verification code in tests.

Add public API changes to the affected package's `PublicAPI.Unshipped.txt`; leave release bookkeeping to the release process. For analyzer changes, update `AnalyzerReleases.Unshipped.md` in the source generator project. Describe breaking changes explicitly, including binary compatibility changes.

Update documentation and samples affected by the change. The website builds from `document/`; the API Reference lives in the separate GitHub Wiki repository and is not updated by a website build. Describe any required reference changes in your pull request.

## Commits and pull requests

Use small commits with this format:

```text
<optional emoji> [optional issue-number] <type>(scope): <description>
```

For example:

```text
🐛 #123 fix(resources): release all resources after a cleanup failure
```

Type, scope and description are required. The scope is one word naming the changed capability. Use `fix`, `feat`, `build`, `ci`, `docs`, `perf`, `refactor`, `test`, `style` or `chore`. Include the issue number when there is one. Emoji are optional; use [gitmoji](https://gitmoji.dev/) if you add one. Mark breaking changes with `!` after the type/scope or a `BREAKING CHANGE:` footer.

In the pull request, explain the problem, the resulting behavior and how you verified it. Link the related issue and mention checks you could not run. Draft pull requests are welcome when you want feedback before finishing a change.
