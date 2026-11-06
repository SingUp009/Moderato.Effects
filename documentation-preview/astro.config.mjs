import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { sourcePath, syncWiki } from './sync-wiki.mjs';

export default defineConfig({
  site: 'https://moderato-effects-wiki-preview.plum-cat-8255.chatgpt.site',
  integrations: [{
    name: 'wiki-source-sync',
    hooks: {
      'astro:server:setup': ({ server, logger }) => {
        let pending = Promise.resolve();
        server.watcher.add(sourcePath);
        server.watcher.on('all', (event, path) => {
          if (!path.startsWith(sourcePath) || !path.endsWith('.md')) return;
          pending = pending.then(syncWiki).catch(error => logger.error(String(error)));
        });
      },
    },
  }, starlight({
    title: 'Moderato.Effects',
    description: 'Wiki draft for review',
    social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/SingUp009/Moderato.Effects' }],
    sidebar: [
      { label: 'Overview', link: '/' },
      { label: 'Start here', items: ['getting-started', 'from-results-to-effects', 'installation', 'adopting-in-an-application'] },
      { label: 'Write effects', items: ['gen-blocks', 'operators', 'error-unions', 'environments'] },
      { label: 'Control execution', items: ['resources', 'time', 'concurrency', 'retry-and-repeat', 'failures'] },
      { label: 'Integrate and diagnose', items: ['hosting', 'http', 'entity-framework-core', 'effect-tracker', 'analyzers', 'caveats'] },
    ],
  })],
});
