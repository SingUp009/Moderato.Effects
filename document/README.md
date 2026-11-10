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

HTML URLs are indexable. Workers serves each Markdown asset as `text/markdown` with `X-Robots-Tag: noindex` so it stays readable without competing with its HTML counterpart in search. The crawl policy allows access to the Markdown and to `/llms.txt`. `npm run build` checks the generated Markdown index and its targets.

## Search discovery

Starlight generates `sitemap-index.xml` and its child sitemaps. The sitemap lists published HTML URLs with their canonical trailing slashes. `public/robots.txt` advertises the sitemap index. The docs build checks the sitemap, page metadata, and internal links.

After deployment, check the public host to confirm that `/robots.txt` advertises `/sitemap-index.xml` and that the index and its listed sitemap files return HTTP 200. Check that `/getting-started` redirects to `/getting-started/`, whose HTML declares that same canonical URL. Check that `/getting-started.md` and `/tutorial/01-results.md` return HTTP 200 with `Content-Type: text/markdown; charset=utf-8` and `X-Robots-Tag: noindex`. Confirm that `/llms.txt` still returns its plain-text index and `/ogp.jpg` returns the social image.

### Google Search Console

1. Add the [URL-prefix property](https://support.google.com/webmasters/answer/10432366) for `https://moderato-effects.009singup.workers.dev/`.
2. Choose [HTML file verification](https://support.google.com/webmasters/answer/9008080). Place Google's downloaded file in `public/` with its original name and content. After an approved deployment, check that the file is accessible at the URL shown in Search Console, then click **Verify**. Keep the file available for periodic rechecks.
3. Submit `sitemap-index.xml` in the [Sitemaps report](https://support.google.com/webmasters/answer/7451001) and check the processing status.
4. Use [URL Inspection](https://support.google.com/webmasters/answer/12482179) for the homepage and important guides. Check their Google-selected canonical URLs and use the [Page indexing report](https://support.google.com/webmasters/answer/7440203) to investigate exclusions.
5. In the [Performance report](https://support.google.com/webmasters/answer/7576553), track queries, pages, impressions, and clicks over time.

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
