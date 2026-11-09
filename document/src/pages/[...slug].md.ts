import type { APIRoute } from 'astro';
import { getMarkdownPages } from '../utils/markdown';

export async function getStaticPaths() {
  return (await getMarkdownPages()).map((page) => ({
    params: { slug: page.id },
    props: { body: page.body },
  }));
}

export const GET: APIRoute = ({ props }) => new Response(props.body, {
  headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
});
