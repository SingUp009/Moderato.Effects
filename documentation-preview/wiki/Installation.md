# Installation

Choose the package your application needs. The integration packages bring Core as a transitive dependency, including its generators and analyzers.

| Package | Use |
|---|---|
| Moderato.Effects | Effects, error unions, resources, concurrency and tracking |
| [Moderato.Effects.Hosting](Hosting) | .NET background services |
| [Moderato.Effects.Http](Http) | ASP.NET Core request execution |
| [Moderato.Effects.EntityFrameworkCore](Entity-Framework-Core) | Owned EF transactions and scope-aware saves |

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
