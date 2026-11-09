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
  assert.ok(links.every((link) => !link.pathname.includes('_Sidebar')));
  assert.match(readFileSync(new URL('../dist/gen-blocks.md', import.meta.url), 'utf8'), /```csharp/);
});
