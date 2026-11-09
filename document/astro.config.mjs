import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightThemeNord from 'starlight-theme-nord';
import react from '@astrojs/react';
import pandacss from '@pandacss/vite';

export default defineConfig({
  site: 'https://moderato-effects.009singup.workers.dev',
  vite: { plugins: [pandacss()] },
  integrations: [
    starlight({
    plugins: [starlightThemeNord()],
    // Starlight declares summary_large_image but adds no image, so the card needs an absolute URL here.
    head: [
      { tag: 'meta', attrs: { property: 'og:image', content: 'https://moderato-effects.009singup.workers.dev/ogp.jpg' } },
      { tag: 'meta', attrs: { property: 'og:image:width', content: '1200' } },
      { tag: 'meta', attrs: { property: 'og:image:height', content: '676' } },
      { tag: 'meta', attrs: { name: 'twitter:image', content: 'https://moderato-effects.009singup.workers.dev/ogp.jpg' } },
    ],
    customCss: ['./src/styles/panda.css'],
    components: { Hero: './src/components/LandingHero.astro', Footer: './src/components/Footer.astro' },
    title: 'Moderato.Effects',
    description: 'Typed errors, explicit dependencies, and managed resources for C#.',
    social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/SingUp009/Moderato.Effects' }],
    sidebar: [
      { label: 'Overview', link: '/' },
      { label: 'Tutorial', items: ['tutorial/01-results', 'tutorial/02-first-effect'] },
      { label: 'Start here', items: ['getting-started', 'from-results-to-effects', 'installation', 'adopting-in-an-application'] },
      { label: 'Write effects', items: ['gen-blocks', 'operators', 'error-unions', 'environments'] },
      { label: 'Control execution', items: ['resources', 'time', 'concurrency', 'retry-and-repeat', 'failures'] },
      { label: 'Integrate and diagnose', items: ['hosting', 'http', 'entity-framework-core', 'effect-tracker', 'opentelemetry', 'analyzers', 'caveats'] },
    ],
  }), react()],
});