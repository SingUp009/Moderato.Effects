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
      { label: 'Integrate and diagnose', items: ['hosting', 'http', 'entity-framework-core', 'effect-tracker', 'analyzers', 'caveats'] },
    ],
  }), react()],
});