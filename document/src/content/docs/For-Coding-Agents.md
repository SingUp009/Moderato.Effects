---
title: Moderato.Effects for Coding Agents
slug: agents
description: Give AI coding agents machine-readable C# effect documentation, a reusable Skill, verified examples, and compiler feedback.
---

Moderato.Effects is an effect system for C# designed around **typed errors, explicit dependencies, and managed resource lifetimes**. Coding agents can discover its APIs through Markdown documentation, follow a dedicated implementation guide, and verify generated code with the C# compiler, Roslyn diagnostics, and executable tests.

This page is for developers who want to use Moderato.Effects **with** a coding agent. For the API-level rules an agent should follow, go straight to the [Agent Guide](/agent-guide/).

## Why Moderato.Effects works with coding agents

| Capability | What it gives an agent |
| --- | --- |
| **Typed failures** | C# 15 error unions make expected failure cases explicit in signatures and checkable by the compiler. |
| **Explicit dependencies** | The `R` environment type identifies services an effect requires. |
| **Managed resources** | `Resource` and `Scope` express acquisition, release, cancellation, and ownership in library constructs. |
| **Compiler feedback** | Roslyn source generators and MEFF analyzers provide diagnostics for invalid API use and scope patterns. |
| **Agent-readable guidance** | Task-oriented Markdown guides and tested C# examples help agents discover the intended patterns instead of guessing APIs. |

These features **support** correct code generation; they do not guarantee it. The [Agent DX evaluation](#agent-dx-measured) measures actual agent behavior.

## Get started

Use **.NET 11 or later** with C# 15 union support. In an application project:

```sh
dotnet add package Moderato.Effects --prerelease
```

Give your coding agent a task along with a concrete documentation entry point:

```text
Implement this operation using Moderato.Effects.

Check the project's .NET SDK and resolved package version.
Read https://moderato-effects.009singup.workers.dev/llms.txt
and the relevant guides before choosing public APIs.
Model expected failures as typed errors. Preserve cancellation,
resource ownership, and cleanup semantics.
Run dotnet build and dotnet test, including relevant failure paths.
```

For a working first effect, follow [Getting started](/getting-started/). To compose effects, manage resources, or handle interruption, use the [Agent Guide](/agent-guide/).

## Markdown documentation for agents

The documentation website and its agent-readable Markdown files are built from **the same source**. An agent can use `llms.txt` to discover relevant pages, then read only the topics needed for its task.

```sh
curl https://moderato-effects.009singup.workers.dev/llms.txt
curl https://moderato-effects.009singup.workers.dev/getting-started.md
curl https://moderato-effects.009singup.workers.dev/resources.md
curl https://moderato-effects.009singup.workers.dev/agent-guide.md
```

| Resource | When to use it |
| --- | --- |
| [`llms.txt`](/llms.txt) | Discover available guides and tutorials. |
| [Agent Guide (Markdown)](/agent-guide.md) | Choose APIs, execution boundaries, and safe composition patterns. |
| [Resources (Markdown)](/resources.md) | Check cleanup order, exit cases, and cancellation behavior. |
| [Concurrency (Markdown)](/concurrency.md) | Compose parallel effects and manage sibling cancellation. |
| [API reference](https://github.com/SingUp009/Moderato.Effects/wiki) | Confirm signatures against the installed package version. |

Always check the application's installed package version: current documentation may describe APIs newer than the dependency in use.

## Agent Skill (preview)

A reusable **`moderato-effects` Agent Skill** is available in a [draft pull request](https://github.com/SingUp009/Moderato.Effects/pull/518). It tells a coding agent how to locate version-appropriate APIs, handle typed errors and resource lifetimes, and validate its changes. **The Skill is not yet part of the repository's default branch.**

To try the current Skill in a Codex project, copy it from the feature branch into your application's `.agents/skills/` directory:

```sh
git clone --depth 1 --branch codex/moderato-effects-skill \
  https://github.com/SingUp009/Moderato.Effects.git moderato-skill-source

mkdir -p .agents/skills
cp -R moderato-skill-source/skills/moderato-effects .agents/skills/
```

Start Codex in the application repository and select `moderato-effects` in the Skill picker, or refer to `$moderato-effects` in your request. See the [Skill installation notes](https://github.com/SingUp009/Moderato.Effects/blob/codex/moderato-effects-skill/skills/README.md) for further details. Update these instructions when the Skill is merged and published.

## Project instructions with AGENTS.md

You can also give an agent persistent guidance in the **application's** `AGENTS.md`. For example:

```md
## Moderato.Effects

Use the installed Moderato.Effects package and check its version.
For API selection and effect semantics, consult:
https://moderato-effects.009singup.workers.dev/agent-guide.md

Use typed errors for expected failures. Keep work deferred
until execution. Preserve caller cancellation and scope-owned cleanup.

Validate changes with dotnet build and dotnet test.
```

This is different from the [Moderato.Effects repository's AGENTS.md](https://github.com/SingUp009/Moderato.Effects/blob/main/AGENTS.md), which gives instructions for **contributing to the library itself**.

## Verify the generated code

Moderato.Effects includes Roslyn analyzers and source generators. The compiler can verify error-union cases and environment constraints; [MEFF diagnostics](/analyzers/) give additional feedback about scope and generator usage.

```sh
dotnet build
dotnet test
```

**Compilation is only the first check.** Test the behaviors a type checker cannot prove: release ordering, cleanup after a defect or cancellation, sibling cancellation, and preservation of original failures.

The [runnable tutorial](/tutorial/00-what-is-an-effect/) and [Agent Guide](/agent-guide/) contain concrete usage patterns. Documentation C# examples are also checked in the repository's [example verification workflow](https://github.com/SingUp009/Moderato.Effects/blob/codex/agent-guide/.github/workflows/docs-examples.yml).

## Agent DX, measured

We also test whether coding agents actually produce correct Moderato.Effects programs—not just code that compiles.

In a **held-out comparison**, coding agents using the improved documentation passed all hidden behavioral tests in **29 of 30 submissions**, compared with **25 of 30** using the original documentation.

| Model | Original documentation | Improved documentation |
| --- | ---: | ---: |
| GPT-6 Luna | 13/15 | 15/15 |
| Claude Haiku 4.5 | 12/15 | 14/15 |
| **Total** | **25/30** | **29/30** |

These 60 submissions covered three held-out tasks, Moderato.Effects `0.1.0-preview.6`, and .NET 11 in isolated Docker fixtures. All four additional passes came from the **Replica read** task; both guides passed every **Catalog fallback** and **Bounded validation** trial. This is evidence for these complete guide snapshots under the tested conditions—not a general success rate or proof of an individual `llms.txt` or Skill effect.

**[Explore the full Agent DX research](/agent-dx/)** to inspect experimental design, model-level results, failure cases, and reproducibility data.
