import { getCollection } from 'astro:content';

export async function getMarkdownPages() {
  const pages = await getCollection('docs', ({ data, filePath }) =>
    !data.draft && filePath?.endsWith('.md') === true
  );
  return pages.sort((a, b) => a.id.localeCompare(b.id));
}
