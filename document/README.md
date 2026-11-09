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
