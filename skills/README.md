# Moderato.Effects Agent Skill

The `moderato-effects` Skill guides coding agents working in an application that uses the library. Start with the [documentation index](https://moderato-effects.009singup.workers.dev/llms.txt) for version-appropriate examples and API guidance.

## Install in a Codex project

From the application repository root, copy the entire `moderato-effects` folder from this checkout into `.agents/skills/`. The entry point should be `.agents/skills/moderato-effects/SKILL.md`. In PowerShell:

```powershell
New-Item -ItemType Directory -Force .agents/skills
Copy-Item -Recurse /path/to/Moderato.Effects/skills/moderato-effects .agents/skills/
```

Start a Codex session in the application repository. Select `moderato-effects` in the skill picker or mention `$moderato-effects` when asking Codex to write or review an effect. Keep the Skill revision aligned with the package version and confirm the available APIs against that package.
