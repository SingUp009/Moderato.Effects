import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
// Expressive Code blocks carry raw source in attributes, which a spec-compliant parser is needed to read.
import { fromHtml } from 'hast-util-from-html';
import { select, selectAll } from 'hast-util-select';
import { inlineScriptHashes } from '../scripts/playground-csp.mjs';

const SOURCES = new URL('../src/content/playground/', import.meta.url);
const DIST = new URL('../dist/', import.meta.url);

const chapterIds = readdirSync(new URL('../src/content/docs/tutorial/', import.meta.url))
  .filter((name) => name.endsWith('.md'))
  .map((name) => name.slice(0, -'.md'.length))
  .sort();

function readHtml(path) {
  return fromHtml(readFileSync(new URL(`${path}index.html`, DIST), 'utf8'));
}

function textOf(node) {
  if (node.type === 'text') return node.value;
  return (node.children ?? []).map(textOf).join('');
}

test('build_WithTutorialChapters_PutsThePlaygroundWithTheChapterCodeOnEachPage', () => {
  for (const id of chapterIds) {
    // Given
    const code = readFileSync(new URL(`${id}.cs`, SOURCES), 'utf8');

    // When
    const page = readHtml(`tutorial/${id}/`);

    // Then
    const playground = select('[data-playground-root]', page);
    assert.ok(playground, id);
    assert.equal(playground.properties.dataChapter, id);
    assert.equal(playground.properties.dataRunner, '/runner/worker.js');
    assert.equal(JSON.parse(textOf(select('script[data-playground-code]', playground))), code, id);
    assert.ok(select('[data-playground-open]', page), id);
  }
});

test('build_WithTutorialChapters_KeepsTheTableOfContentsForWhenThePlaygroundIsClosed', () => {
  for (const id of chapterIds) {
    // Given
    const path = `tutorial/${id}/`;

    // When
    const page = readHtml(path);

    // Then
    assert.ok(select('.playground-toc .right-sidebar-panel', page), id);
  }
});

test('build_WithOtherGuides_LeavesThemWithoutAPlayground', () => {
  // Given
  const path = 'getting-started/';

  // When
  const page = readHtml(path);

  // Then
  assert.equal(select('[data-playground-root]', page), undefined);
  assert.equal(select('.playground-toc', page), undefined);
});

test('build_WithTutorialPages_AllowsOnlyTheirOwnInlineScriptsInTheCsp', () => {
  // Given
  const expected = inlineScriptHashes(fileURLToPath(DIST));

  // When
  const headers = readFileSync(new URL('_headers', DIST), 'utf8');

  // Then
  assert.ok(expected.length > 0);
  for (const path of ['/tutorial/*', '/runner/*']) {
    const rule = new RegExp(`^${path.replace(/[/*]/g, '\\$&')}\\r?\\n\\s+Content-Security-Policy: (.+)$`, 'm').exec(headers);
    assert.ok(rule, `no CSP rule for ${path}`);
    const scriptSrc = rule[1].split('; ').find((directive) => directive.startsWith('script-src ')).split(' ');
    assert.deepEqual(scriptSrc.filter((source) => source.startsWith("'sha256-")), expected, path);
    assert.ok(scriptSrc.includes("'wasm-unsafe-eval'"), path);
    assert.ok(!scriptSrc.includes("'unsafe-inline'") && !scriptSrc.includes("'unsafe-eval'"), path);
  }
});
