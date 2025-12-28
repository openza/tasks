import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import sitemap from '@astrojs/sitemap';

export default defineConfig({
  site: 'https://openza.github.io',
  base: '/tasks',
  integrations: [
    sitemap(),
    starlight({
      title: 'Openza Tasks',
      description: 'Documentation for Openza Tasks - A beautiful task management app for Linux',
      logo: {
        src: './src/assets/logo.svg',
      },
      social: {
        github: 'https://github.com/openza/tasks',
      },
      customCss: [
        './src/styles/custom.css',
      ],
      sidebar: [
        {
          label: 'Getting Started',
          items: [
            { label: 'Introduction', slug: 'getting-started/introduction' },
            { label: 'Installation', slug: 'getting-started/installation' },
            { label: 'Configuration', slug: 'getting-started/configuration' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'Todoist Setup', slug: 'guides/todoist-setup' },
            { label: 'Microsoft To-Do', slug: 'guides/mstodo-setup' },
          ],
        },
        {
          label: 'Features',
          autogenerate: { directory: 'features' },
        },
        {
          label: 'Development',
          items: [
            { label: 'Building from Source', slug: 'development/building' },
            { label: 'Architecture', slug: 'development/architecture' },
            { label: 'Contributing', slug: 'development/contributing' },
          ],
        },
      ],
    }),
  ],
});
