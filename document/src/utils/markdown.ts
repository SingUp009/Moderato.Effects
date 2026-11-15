import { getCollection } from 'astro:content';

export async function getMarkdownPages() {
  const pages = await getCollection('docs', ({ id, data, filePath }) =>
    id !== 'agents' && !data.draft && filePath?.endsWith('.md') === true
  );
  return pages.sort((a, b) => a.id.localeCompare(b.id));
}
