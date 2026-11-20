import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { parse } from 'ultrahtml';
import { querySelector, querySelectorAll } from 'ultrahtml/selector';

const SOURCES = new URL('../src/content/tour/', import.meta.url);
const DIST = new URL('../dist/tour/', import.meta.url);

const chapterIds = readdirSync(SOURCES)
  .filter((name) => name.endsWith('.md'))
  .map((name) => name.slice(0, -'.md'.length))
  .sort();

function readPage(path) {
  return parse(readFileSync(new URL(`${path}index.html`, DIST), 'utf8'));
}

function chaptersOf(page) {
  const data = querySelector(page, 'script[data-tour-chapters]');
  return JSON.parse(data.children.map((child) => child.value).join(''));
}

test('build_WithTourChapters_EmbedsEveryChapterWithItsCode', () => {
  // Given
  const expected = chapterIds.map((id) => ({
    id,
    code: readFileSync(new URL(`${id}.cs`, SOURCES), 'utf8'),
  }));

  // When
  const chapters = chaptersOf(readPage(`${chapterIds[0]}/`));

  // Then
  assert.deepEqual(chapters.map(({ id, code }) => ({ id, code })), expected);
});

test('build_WithTourChapters_ShowsOnlyTheRequestedChapter', () => {
  for (const [index, id] of chapterIds.entries()) {
    // Given
    const path = `${id}/`;

    // When
    const page = readPage(path);

    // Then
    const visible = querySelectorAll(page, 'article[data-tour-chapter]')
      .filter((article) => !('hidden' in article.attributes))
      .map((article) => article.attributes['data-tour-chapter']);
    assert.deepEqual(visible, [id]);
    assert.equal(querySelector(page, '.tour-prev').attributes.href, index > 0 ? `/tour/${chapterIds[index - 1]}/` : undefined);
    assert.equal(querySelector(page, '.tour-next').attributes.href, index < chapterIds.length - 1 ? `/tour/${chapterIds[index + 1]}/` : undefined);
  }
});

test('build_TourIndex_ShowsTheFirstChapter', () => {
  // Given
  const first = chapterIds[0];

  // When
  const page = readPage('');

  // Then
  const visible = querySelectorAll(page, 'article[data-tour-chapter]')
    .filter((article) => !('hidden' in article.attributes))
    .map((article) => article.attributes['data-tour-chapter']);
  assert.deepEqual(visible, [first]);
  assert.equal(querySelector(page, '.tour').attributes['data-runner'], '/tour/runner/worker.js');
});
