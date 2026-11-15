---
title: Moderato.Effects for Coding Agents
slug: agents
description: Set up coding agents to use Moderato.Effects with Markdown documentation, an Agent Skill, and project instructions.
---

This page lists the resources and instructions available to help coding agents work with **Moderato.Effects**. If you are implementing or reviewing C# effects with an agent, start here.

## Getting started

Moderato.Effects requires .NET 11 or later and C# 15 union support. Add the package to your application:

```sh
dotnet add package Moderato.Effects --prerelease
```

You can then give your agent a task such as:

```text
Implement this operation using Moderato.Effects.

Check the SDK and installed package version, then read
https://moderato-effects.009singup.workers.dev/llms.txt
and the relevant guides before choosing an API.

Use typed errors for expected failures, preserve cancellation
and resource cleanup, and build and test the implementation.
```

For a runnable example, see [Getting started](/getting-started/). For API choices and effect semantics, see the [Agent Guide](/agent-guide/).

## Documentation for agents

The documentation is available as Markdown as well as HTML. Agents can read `llms.txt` to discover the relevant guides and fetch individual pages without parsing the website.

```sh
curl https://moderato-effects.009singup.workers.dev/llms.txt
curl https://moderato-effects.009singup.workers.dev/getting-started.md
curl https://moderato-effects.009singup.workers.dev/agent-guide.md
curl https://moderato-effects.009singup.workers.dev/resources.md
```

The [Agent Guide](/agent-guide/) covers execution, typed errors, environments, cancellation, resource ownership, and verification. The [API reference](https://github.com/SingUp009/Moderato.Effects/wiki) provides further detail.

**Check the installed package version.** Documentation may describe APIs newer than those available in your application.

## Moderato.Effects Skill

The [`moderato-effects` Agent Skill](https://github.com/SingUp009/Moderato.Effects/tree/main/skills/moderato-effects) provides task-specific instructions for using the library in an application. It helps agents find documentation, select APIs appropriate to the installed version, and check error handling, cancellation, and resource lifetimes.

For **Codex**, copy the Skill directory into the application repository:

```sh
git clone --depth 1 https://github.com/SingUp009/Moderato.Effects.git moderato-effects-source
mkdir -p .agents/skills
cp -R moderato-effects-source/skills/moderato-effects .agents/skills/
```

The entry point is `.agents/skills/moderato-effects/SKILL.md`. Start Codex in the application repository, select the Skill from the picker, or mention `$moderato-effects` in your request.

See the [Skill README](https://github.com/SingUp009/Moderato.Effects/blob/main/skills/README.md) for installation notes, including PowerShell.

## Project instructions with AGENTS.md

Add library-specific guidance to your **application's** `AGENTS.md` so coding agents can find it while working in the repository. For example:

```md
## Moderato.Effects

Check the project's .NET SDK and installed Moderato.Effects version.
Use https://moderato-effects.009singup.workers.dev/agent-guide.md
for API selection and effect semantics.

Keep expected failures typed. Defer work until execution.
Preserve caller cancellation and scope-owned resource cleanup.
Validate changes with dotnet build and dotnet test.
```

The [`AGENTS.md` in the Moderato.Effects repository](https://github.com/SingUp009/Moderato.Effects/blob/main/AGENTS.md) is for people and agents **contributing to the library**; the example above is for applications that **use** it.

## Verify generated code

Use the C# compiler and the library's Roslyn diagnostics to validate an agent's implementation:

```sh
dotnet build
dotnet test
```

Consult the [MEFF analyzer reference](/analyzers/) when diagnostics appear, and use the [Agent Guide verification checklist](/agent-guide/#verify-the-implementation) to review expected failures, cancellation, cleanup, and concurrency. Passing a build does not by itself prove those runtime behaviors are correct.

## How well do agents use Moderato.Effects?

We evaluate coding agents on runnable C# tasks and measure whether their implementations pass behavioral tests. The methods and results are published separately at [Moderato.Effects Agent DX](https://moderato-effects-agent-dx.009singup.workers.dev/).
