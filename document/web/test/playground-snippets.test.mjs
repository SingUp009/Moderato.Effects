import assert from 'node:assert/strict';
import test from 'node:test';
import { findSnippet } from '../src/components/playground/snippets.ts';

const PROGRAM = [
  'using Moderato.Effects;',
  '',
  'var accepted = 1;',
  'Console.WriteLine(accepted);',
  '',
  'static Result<int, string> ValidateQuantity(int quantity)',
  '{',
  '    if (quantity <= 0)',
  '',
  '        return new Err<string>("Bad");',
  '}',
].join('\n');

test('findSnippet_WithMatchingLines_ReturnsTheirRange', () => {
  // Given
  const snippet = 'var accepted = 1;\nConsole.WriteLine(accepted);';

  // When
  const range = findSnippet(PROGRAM, snippet);

  // Then
  assert.deepEqual(range, [{ from: 3, to: 4 }]);
});

test('findSnippet_WithDifferentIndentationAndModifiers_StillMatches', () => {
  // Given
  const snippet = '    private static Result<int, string> ValidateQuantity(int quantity)\n    {';

  // When
  const range = findSnippet(PROGRAM, snippet);

  // Then
  assert.deepEqual(range, [{ from: 6, to: 7 }]);
});

test('findSnippet_WithBlankLinesInTheProgram_SkipsThem', () => {
  // Given
  const snippet = 'if (quantity <= 0)\n    return new Err<string>("Bad");';

  // When
  const range = findSnippet(PROGRAM, snippet);

  // Then
  assert.deepEqual(range, [{ from: 8, to: 10 }]);
});

test('findSnippet_WithLinesApartOrAssigned_MatchesEachLineInOrder', () => {
  // Given
  const program = 'var result = await effect.RunAsync(a);\nConsole.WriteLine(result);\nawait effect.RunAsync(b);';
  const snippet = 'await effect.RunAsync(a);\nawait effect.RunAsync(b);';

  // When
  const range = findSnippet(program, snippet);

  // Then
  assert.deepEqual(range, [{ from: 1, to: 1 }, { from: 3, to: 3 }]);
});

test('findSnippet_WithCodeNotInTheProgram_ReturnsNoRanges', () => {
  // Given
  const snippet = 'Effect<string, Never, EmptyEnvironment>';

  // When
  const range = findSnippet(PROGRAM, snippet);

  // Then
  assert.deepEqual(range, []);
});

test('findSnippet_WithOnlyBlankLines_ReturnsNoRanges', () => {
  // Given
  const snippet = '\n  \n';

  // When
  const range = findSnippet(PROGRAM, snippet);

  // Then
  assert.deepEqual(range, []);
});
