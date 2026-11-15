---
title: Moderato.Effects for Coding Agents
slug: agents
description: Help coding agents write and verify C# effects with typed errors, explicit dependencies, and managed resource lifetimes.
---

Moderato.Effects gives coding agents a way to compose C# operations with explicit dependencies and managed resource lifetimes. Effect signatures show which services an operation needs and which failures it can return as C# discriminated unions. Resource scopes release acquired values when work succeeds, fails, or is canceled. Agents can consult verified examples and use compiler and analyzer feedback as they build.

## Get started

Use the .NET 11 SDK or later with C# 15 union support. From your application's project directory, add the package if needed.

```sh
dotnet add package Moderato.Effects --prerelease
```

Give your agent a task and include this prompt.

```text
Use Moderato.Effects to implement this operation.
Check the project's SDK and package version. Start with
https://moderato-effects.009singup.workers.dev/llms.txt and consult
the Agent Guide and related pages before choosing APIs.
Model expected failures as typed errors, pass cancellation through,
and scope cleanup to the code that acquires each resource.
Run dotnet build and dotnet test. Test failure and cleanup paths
that apply to the operation.
```

Follow [Getting started](/getting-started/) to create a project and run your first effect. Open the [Agent Guide](/agent-guide/) when you are ready to compose effects in your application.

## Documentation for agents

Start at [`/llms.txt`](/llms.txt) to find Markdown guides by topic. Fetch the pages relevant to the task. The website and these Markdown files use the same guide source.

```sh
curl https://moderato-effects.009singup.workers.dev/llms.txt
curl https://moderato-effects.009singup.workers.dev/getting-started.md
curl https://moderato-effects.009singup.workers.dev/resources.md
```

Use the [Agent Guide in Markdown](/agent-guide.md) for API selection, resource and cancellation rules, and verified C# examples. Check signatures in the [API reference](https://github.com/SingUp009/Moderato.Effects/wiki) against your installed package. The [runnable tutorial](/tutorial/00-what-is-an-effect/) shows how these APIs fit together.

## Project guidance

In your application's repository instructions, record the SDK and package versions, the build and test commands, and a link to [the Agent Guide in Markdown](/agent-guide.md). The library repository's [`AGENTS.md`](https://github.com/SingUp009/Moderato.Effects/blob/main/AGENTS.md) guides contributors working on Moderato.Effects itself.

## Verify changes

The C# compiler checks types such as the cases in an error union and the service constraints on an effect. The package includes Roslyn analyzers and source generators. Use the [MEFF diagnostics reference](/analyzers/) to look up any messages they report.

```sh
dotnet build
dotnet test
```

A successful build checks types and analyzer rules. Use behavioral tests to check [concurrency and cancellation](/concurrency/) and [resource cleanup](/resources/#release-rules), such as whether the operation waits for a canceled child's release before returning.

## Implementation guide

- <a id="write-and-run-a-first-effect" href="/agent-guide/#write-and-run-a-first-effect">Write and run a first effect</a>
- <a id="manage-a-resource-with-asynchronous-cleanup" href="/agent-guide/#manage-a-resource-with-asynchronous-cleanup">Manage a resource with asynchronous cleanup</a>
- <a id="verify-the-implementation" href="/agent-guide/#verify-the-implementation">Verify the implementation</a>

## Agent DX, measured

In a held-out evaluation, agents using the frozen guide passed all hidden tests in 29 of 30 submissions. With the original guide, 25 of 30 passed.

| Model | Original guide | Frozen guide |
| --- | ---: | ---: |
| GPT-6 Luna | 13/15 | 15/15 |
| Claude Haiku 4.5 | 12/15 | 14/15 |

The 60 submissions covered three held-out tasks using Moderato.Effects `0.1.0-preview.6` and .NET 11 in isolated Docker fixtures. All four additional passes came from Replica read. Both models passed Catalog fallback and Bounded validation in every trial under both guides. The comparison measures the two complete guide snapshots in this setting.

**[Explore the Agent DX research →](/agent-dx/)**
