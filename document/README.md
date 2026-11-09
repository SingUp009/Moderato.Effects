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

Enable builds for non-production branches so pull requests receive Cloudflare build checks and preview URLs. The `.node-version` file selects Node.js 26. Workers Builds manages its build token; no Cloudflare deployment secret is required in GitHub Actions.

Keep the GitHub Actions document job until both a Cloudflare production build and a pull request build have succeeded. After that, remove the duplicate site job; the runnable C# tutorial remains in the .NET test job.

Workers Logs is enabled in `wrangler.jsonc`. Static assets are served directly, so an asset request does not necessarily produce a Worker invocation log.
