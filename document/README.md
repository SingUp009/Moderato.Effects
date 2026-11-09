# Moderato.Effects documentation

Published at [moderato-effects.009singup.workers.dev](https://moderato-effects.009singup.workers.dev).

Edit the Markdown and MDX in `src/content/docs/`. Starlight builds the site directly from these files.

```sh
npm ci
npm run dev
```

## Cloudflare Workers

The site is static. Workers serves the generated `dist/` directory with its static assets support; no server adapter is needed.

Authenticate with your Cloudflare account, then build and deploy:

```sh
npx wrangler login
npm run deploy
```

`wrangler.jsonc` names the Worker `moderato-effects` and uses the generated `404.html` for unknown routes. Use `npx wrangler deploy --dry-run` after `npm run build` to check deployment configuration without publishing.

This site does not update the separate GitHub Wiki repository.

## Markdown for agents

`/llms.txt` indexes the Markdown guides and tutorial chapters. Each guide is also available with a `.md` suffix, for example `/getting-started.md` or `/tutorial/01-results.md`. These files are generated from the same content as the HTML pages during the build. Drafts and the MDX landing page are excluded.

```sh
curl https://moderato-effects.009singup.workers.dev/llms.txt
curl https://moderato-effects.009singup.workers.dev/getting-started.md
```

HTML URLs continue to return HTML; Markdown URLs return `text/markdown`. No request-time conversion is needed. `npm run build` also checks the generated Markdown index and its targets.

## Automatic builds

Workers Builds owns the website build and deployment. Connect the GitHub repository to the `moderato-effects` Worker and use these settings:

| Setting | Value |
| --- | --- |
| Repository | `SingUp009/Moderato.Effects` |
| Production branch | `main` |
| Root directory | `document` |
| Build command | `npm run build` |
| Deploy command | `npx wrangler deploy` |
| Preview command | `npx wrangler preview` |
| Build watch include paths | `document/*` |

Enable builds for non-production branches so pull requests receive Cloudflare build checks and preview URLs. The `.node-version` file pins Node.js 26.13.0, matching the locally verified runtime. Workers Builds manages its build token; no Cloudflare deployment secret is required in GitHub Actions.

Cloudflare reports website builds on GitHub pull requests. GitHub Actions skips changes confined to `document/`; changes to the runnable C# tutorial still run the .NET test job.

Workers Logs is enabled in `wrangler.jsonc`. Static assets are served directly, so an asset request does not necessarily produce a Worker invocation log.
