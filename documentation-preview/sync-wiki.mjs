import { readFile, readdir, writeFile, rm, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const source = new URL('./wiki/', import.meta.url);
const target = new URL('./src/content/docs/', import.meta.url);
export const sourcePath = fileURLToPath(source);

export async function syncWiki() {
  await mkdir(target, { recursive: true });
  const files = (await readdir(source)).filter(name => name.endsWith('.md') && !name.startsWith('_'));
  const slugs = new Map(files.map(name => [name.slice(0, -3), name === 'Home.md' ? '' : name.slice(0, -3).toLowerCase()]));
  const expected = new Set([...slugs.values()].map(slug => `${slug || 'index'}.md`));
  for (const name of await readdir(target)) {
    if (!expected.has(name)) await rm(new URL(name, target), { recursive: true });
  }
  for (const name of files) {
    const original = await readFile(new URL(name, source), 'utf8');
    const title = original.match(/^# (.+)$/m)?.[1] ?? name.slice(0, -3);
    const body = original.replace(/^# .+\n+/, '').replace(/\]\(([^)]+)\)/g, (match, destination) => {
      const [page, anchor] = destination.split('#');
      return slugs.has(page) ? `](/${slugs.get(page)}${slugs.get(page) ? '/' : ''}${anchor ? '#' + anchor : ''})` : match;
    });
    const slug = slugs.get(name.slice(0, -3));
    const destination = new URL(`${slug || 'index'}.md`, target);
    const content = `---\ntitle: ${JSON.stringify(title)}\n---\n\n${body}`;
    const previous = await readFile(destination, 'utf8').catch(error => {
      if (error.code === 'ENOENT') return null;
      throw error;
    });
    if (previous !== content) await writeFile(destination, content);
  }
  console.log(`Copied ${files.length} Wiki pages to ${fileURLToPath(target)}`);
}

await syncWiki();
