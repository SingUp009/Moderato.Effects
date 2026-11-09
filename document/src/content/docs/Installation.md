---
title: Installation
---

# Installation

Choose the package your application needs. The integration packages bring Core as a transitive dependency, including its generators and analyzers.

| Package | Use |
|---|---|
| Moderato.Effects | Effects, error unions, resources, concurrency and tracking |
| [Moderato.Effects.Hosting](/hosting/) | .NET background services |
| [Moderato.Effects.Http](/http/) | ASP.NET Core request execution |
| [Moderato.Effects.EntityFrameworkCore](/entity-framework-core/) | Owned EF transactions and scope-aware saves |

```sh
dotnet add package Moderato.Effects
```

For an integration:

```sh
dotnet add package Moderato.Effects.EntityFrameworkCore
```

## Build from source

Clone [the repository](https://github.com/SingUp009/Moderato.Effects) and use the SDK pinned in `global.json`. Projects can target `net11.0` or later with C# 15.

```sh
git clone https://github.com/SingUp009/Moderato.Effects.git
cd Moderato.Effects
dotnet build -c Release
```
