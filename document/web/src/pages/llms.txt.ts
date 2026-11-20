import type { APIRoute } from 'astro';
import { getMarkdownPages } from '../utils/markdown';

export const GET: APIRoute = async ({ site }) => {
  const links = (await getMarkdownPages()).map((page) =>
    `- [${page.data.title}](${new URL(`${page.id}.md`, site)})`
  );
  return new Response([
    '# Moderato.Effects',
    '> An effect system for C# with typed errors, explicit dependencies, and managed resources.',
    '## Documentation',
    links.join('\n'),
  ].join('\n\n') + '\n', {
    headers: { 'Content-Type': 'text/plain; charset=utf-8' },
  });
};
