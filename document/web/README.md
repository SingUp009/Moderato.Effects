# Moderato.Effects documentation

Published at [moderato-effects.009singup.workers.dev](https://moderato-effects.009singup.workers.dev).

Edit the Markdown and MDX in `src/content/docs/`. Starlight builds the site directly from these files.

```sh
npm ci
npm run dev
```

## Cloudflare Workers

The site is static. Workers serves the generated `dist/` directory with its static assets support; no server adapter is needed.

Authenticate with your Cloudflare account, then build and deploy. This also builds the tutorial playground's runner, so it needs the .NET SDK and the `wasm-tools` workload:

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

## Tutorial playground

Each tutorial chapter has an editor beside it where the reader edits and runs the chapter's code. One side panel shows at a time, so the text keeps its room: with the editor open, it takes the place of "On this page" and the site navigation folds to a narrow rail; with it closed, the page is the plain tutorial. A sidebar button at the bottom left switches between the two. Wide screens start with the editor, narrow ones with the navigation, where the editor opens as a sheet over the lower part of the page, and the browser remembers the reader's choice. `src/components/playground/PageSidebar.astro` overrides Starlight's `PageSidebar` to add it.

The starting code is `src/content/playground/<id>.cs`, named after the tutorial page; every tutorial chapter needs one, and the build fails without it. Edits last for the browser session; Reset restores the starting code.

As the reader scrolls, the editor highlights the lines that the C# snippet being read shows, and stops at the "Try it" section. Snippets are matched to the editor's code line by line, ignoring indentation and access modifiers, so keep a chapter's `.cs` close to the tutorial's code; a snippet with no match highlights nothing.

The code compiles and runs in a Web Worker on the .NET WebAssembly runtime, built from `document/tour/wasm-runner`. The runtime downloads only when the editor first opens. Build it once before `npm run dev` or `npm run build`; it lands in `public/runner/`, which is not committed:

```sh
dotnet workload install wasm-tools
npm run runner
```

Without it, the pages still build but the editor cannot run code.

`/tutorial/*` and `/runner/*` are served with a Content-Security-Policy. After `astro build`, `scripts/playground-csp.mjs` hashes the inline scripts of the built tutorial pages and appends the policy to `dist/_headers`, so it follows Starlight and Astro updates without editing. Scripts load only from the site itself or by those hashes; `'wasm-unsafe-eval'` lets the .NET runtime compile WebAssembly. `style-src` keeps `'unsafe-inline'` because Expressive Code colors code with style attributes. `npm run build` checks the policy. To check it in a browser, serve `dist/` with `npx wrangler dev`; `npm run dev` does not apply `_headers`. `public/_headers` lets browsers keep the runner's hashed files for good; `dotnet.js` is revalidated because it names the files of the current build.

## Agent DX research

`/agent-dx/` uses the existing Starlight layout and a React chart for development comparisons. The build exports the results as `/agent-dx/results.json`. The guide archive contains both 30-file input snapshots and their SHA-256 hashes. Keep that archive intact for reproducing the first evaluation.

Once all registered live trials have a recorded outcome, use `experiments/holdout/summarize.mts` in the AgentDX checkout to generate `src/data/holdout-results.json`. The page checks the trial count and both snapshot hashes before rendering that result.

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

The Documentation site workflow (`.github/workflows/docs-site.yml`) builds the website, and deploys it on pushes to `main`. Workers Builds cannot do this: its build image has no .NET SDK, which the playground's runner needs. Keep the `moderato-effects` Worker disconnected from Workers Builds so the two do not both deploy.

The workflow installs the SDK from `global.json` and the `wasm-tools` workload, builds the runner, runs `npm run build` with its tests, and deploys with `npx wrangler deploy`. It runs for changes to `document/web/`, `document/tour/`, and the library and source generator the runner compiles against. Pull requests get the build and tests without a deploy.

Deployment needs two repository secrets:

| Secret | Value |
| --- | --- |
| `CLOUDFLARE_API_TOKEN` | An API token with the **Edit Cloudflare Workers** permission |
| `CLOUDFLARE_ACCOUNT_ID` | The account that owns the `moderato-effects` Worker |

The `.node-version` file pins Node.js 26.13.0, matching the locally verified runtime.

The main .NET workflow skips changes confined to `document/web/` and `document/tour/`. Changes to the runnable C# tutorial in `document/tutorial/` still run the .NET test job.

The Documentation examples workflow compiles C# blocks from the core guides, including on documentation-only changes. The build supplies context for excerpts and fills in explicit placeholders in Environments and Gen blocks. Run `node .github/scripts/verify-guide-examples.mjs` from the repository root to repeat the check.

Workers Logs is enabled in `wrangler.jsonc`. Static assets are served directly, so an asset request does not necessarily produce a Worker invocation log.
