import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

test('the agent index links to published Markdown guides', () => {
  const index = readFileSync(new URL('../dist/llms.txt', import.meta.url), 'utf8');
  const links = [...index.matchAll(/\]\((https:\/\/[^)]+)\)/g)].map((match) => new URL(match[1]));
  assert.ok(links.length > 0);
  assert.equal(new Set(links.map((link) => link.pathname)).size, links.length);

  for (const link of links) {
    assert.equal(link.origin, 'https://moderato-effects.009singup.workers.dev');
    assert.ok(link.pathname.endsWith('.md'));
    const markdown = readFileSync(new URL(`../dist${link.pathname}`, import.meta.url), 'utf8');
    assert.match(markdown, /^\s*# /);
    assert.doesNotMatch(markdown, /<!doctype html|<html/i);
    const htmlPath = link.pathname.replace(/\.md$/, '/index.html');
    assert.match(readFileSync(new URL(`../dist${htmlPath}`, import.meta.url), 'utf8'), /<!doctype html/i);
  }

  assert.ok(links.some((link) => link.pathname === '/tutorial/01-results.md'));
  assert.ok(links.some((link) => link.pathname === '/tutorial/02-first-effect.md'));
  assert.ok(links.some((link) => link.pathname === '/agents.md'));
  const agentsHtml = readFileSync(new URL('../dist/agents/index.html', import.meta.url), 'utf8');
  assert.match(agentsHtml, /<a [^>]*href="\/agents\/"[^>]*aria-current="page"/);
  assert.ok(links.every((link) => !link.pathname.includes('_Sidebar')));
  assert.match(readFileSync(new URL('../dist/gen-blocks.md', import.meta.url), 'utf8'), /```csharp/);
});

test('chapter links resolve from the Markdown asset URL', () => {
  const index = readFileSync(new URL('../dist/llms.txt', import.meta.url), 'utf8');
  for (const [page] of index.matchAll(/https:\/\/[^)]+\/tutorial\/[^)]+\.md/g)) {
    const markdown = readFileSync(new URL(`../dist${new URL(page).pathname}`, import.meta.url), 'utf8');
    for (const [, link] of markdown.matchAll(/\]\(((?:\/tutorial\/|\.\.?\/)[^)]+)\)/g)) {
      const target = new URL(link, page);
      assert.doesNotThrow(() => readFileSync(new URL(`../dist${target.pathname}index.html`, import.meta.url)), `${page} links to ${target.href}`);
    }
  }
});
