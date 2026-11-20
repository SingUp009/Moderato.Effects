import type { APIRoute } from 'astro';
import { getMarkdownPages } from '../utils/markdown';

export async function getStaticPaths() {
  return (await getMarkdownPages()).flatMap((page) => {
    const markdown = page.body ?? '';
    const body = /^\s*# /.test(markdown) ? markdown : `# ${page.data.title}\n\n${markdown}`;
    const slugs = page.id === 'agent-guide' ? [page.id, 'agents'] : [page.id];
    return slugs.map((slug) => ({ params: { slug }, props: { body } }));
  });
}

export const GET: APIRoute = ({ props }) => new Response(props.body, {
  headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
});
