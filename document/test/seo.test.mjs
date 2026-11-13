import assert from 'node:assert/strict';
import { globSync, readFileSync, statSync } from 'node:fs';
import { sep } from 'node:path';
import test from 'node:test';
import { parse } from 'ultrahtml';
import { querySelector, querySelectorAll } from 'ultrahtml/selector';

const SITE = 'https://moderato-effects.009singup.workers.dev';
const DIST = new URL('../dist/', import.meta.url);

function readOutput(path) {
  return readFileSync(new URL(path, DIST), 'utf8');
}

function locations(xml, root, entry) {
  const sitemap = querySelector(parse(xml), root);
  assert.equal(sitemap?.attributes.xmlns, 'http://www.sitemaps.org/schemas/sitemap/0.9');
  return querySelectorAll(sitemap, `${entry} > loc`).map((node) => node.children[0].value.trim());
}

const pagePaths = globSync('**/index.html', { cwd: DIST })
  .map((path) => '/' + path.split(sep).join('/').replace(/index\.html$/, ''));

test('build_WithPublishedPages_EmitsCanonicalSitemap', () => {
  // Given
  const sitemapIndex = new URL('/sitemap-index.xml', SITE);

  // When
  const robots = readOutput('robots.txt');
  const sitemaps = locations(readOutput('sitemap-index.xml'), 'sitemapindex', 'sitemap');
  const pages = sitemaps.flatMap((location) => {
    const sitemap = new URL(location);
    assert.equal(sitemap.origin, SITE);
    return locations(readOutput(sitemap.pathname.slice(1)), 'urlset', 'url');
  });

  // Then
  assert.ok(robots.split(/\r?\n/).some((line) => line.trim() === `Sitemap: ${sitemapIndex.href}`));
  assert.doesNotMatch(robots, /^[ \t]*Disallow:[ \t]*[^\s#]/im);
  assert.ok(pagePaths.includes('/'));
  assert.equal(new Set(pages).size, pages.length, 'duplicate sitemap URL');
  assert.deepEqual(new Set(pages), new Set(pagePaths.map((path) => new URL(path, SITE).href)));
});

test('build_WithPublishedPages_EmitsUniqueSearchAndSocialMetadata', () => {
  // Given
  const titles = new Set();
  const descriptions = new Set();
  const socialImage = new URL('/ogp.jpg', SITE).href;

  for (const path of pagePaths) {
    // When
    const page = parse(readOutput(`.${path}index.html`));
    const head = querySelector(page, 'head');

    // Then
    const title = querySelector(head, 'title')?.children[0]?.value.trim();
    const description = querySelector(head, 'meta[name="description"]')?.attributes.content?.trim();
    const canonical = querySelectorAll(head, 'link[rel="canonical"]');
    const ogTitle = querySelector(head, 'meta[property="og:title"]')?.attributes.content;
    const ogDescription = querySelector(head, 'meta[property="og:description"]')?.attributes.content;
    const ogUrl = querySelector(head, 'meta[property="og:url"]')?.attributes.content;
    const ogImage = querySelector(head, 'meta[property="og:image"]')?.attributes.content;
    const twitterImage = querySelector(head, 'meta[name="twitter:image"]')?.attributes.content;
    const twitterCard = querySelector(head, 'meta[name="twitter:card"]')?.attributes.content;
    assert.ok(title && !titles.has(title), `missing or duplicate title on ${path}`);
    assert.ok(description && !descriptions.has(description), `missing or duplicate description on ${path}`);
    assert.deepEqual(canonical.map((link) => link.attributes.href), [new URL(path, SITE).href], path);
    assert.ok(ogTitle && title.includes(ogTitle), path);
    assert.equal(ogDescription, description, path);
    assert.equal(ogUrl, canonical[0].attributes.href, path);
    assert.equal(ogImage, socialImage, path);
    assert.equal(twitterImage, socialImage, path);
    assert.equal(twitterCard, 'summary_large_image', path);
    for (const meta of querySelectorAll(head, 'meta[name]')) {
      if (['robots', 'googlebot'].includes(meta.attributes.name.toLowerCase())) {
        assert.doesNotMatch(meta.attributes.content, /\b(?:noindex|none)\b/i, path);
      }
    }
    titles.add(title);
    descriptions.add(description);
  }

  assert.ok(statSync(new URL('ogp.jpg', DIST)).isFile());
});

test('build_WithPublishedPages_ResolvesInternalLinks', () => {
  // Given
  const documents = new Map(pagePaths.map((path) => [path, parse(readOutput(`.${path}index.html`))]));
  const idsByPath = new Map([...documents].map(([path, page]) => [
    path, new Set(querySelectorAll(page, '[id]').map((node) => node.attributes.id)),
  ]));

  for (const [path, page] of documents) {
    for (const link of querySelectorAll(page, 'a[href]')) {
      // When
      const href = link.attributes.href;
      const target = new URL(href, new URL(path, SITE));

      // Then
      if (target.origin !== SITE) continue;

      const ids = idsByPath.get(target.pathname);
      if (ids) {
        if (target.hash) {
          assert.ok(ids.has(decodeURIComponent(target.hash.slice(1))), `${path} links to missing anchor ${href}`);
        }
      } else {
        const asset = statSync(new URL(`.${target.pathname}`, DIST), { throwIfNoEntry: false });
        assert.ok(asset?.isFile(), `${path} links to missing or noncanonical target ${href}`);
      }
    }
  }
});

test('build_WithMarkdownAssets_PreservesHeadersAndExcludesThemFromSearch', () => {
  // Given
  const headers = readOutput('_headers');

  // When
  const markdown = headers.split(/\r?\n(?=\S)/).find((rule) => rule.split(/\r?\n/, 1)[0] === '/*.md');

  // Then
  assert.ok(markdown, 'missing Markdown asset header rule');
  assert.match(markdown, /^\s+Content-Type:\s*text\/markdown;\s*charset=utf-8\s*$/im);
  assert.match(markdown, /^\s+X-Robots-Tag:\s*noindex\s*$/im);
});
