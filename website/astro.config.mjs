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
      description: 'Documentation for Openza Tasks - A local-first task organizer for Linux and Windows',
      logo: {
        src: './src/assets/logo.svg',
      },
      favicon: 'favicon.svg',
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
          label: 'Using the App',
          items: [
            { label: 'App Layout', slug: 'features/dashboard' },
            { label: 'Tasks', slug: 'features/tasks' },
            { label: 'Projects', slug: 'features/projects' },
            { label: 'Labels', slug: 'features/labels' },
            { label: 'Backup & Restore', slug: 'features/backup' },
            { label: 'Import from Markdown', slug: 'features/import' },
            { label: 'Export to Markdown', slug: 'features/export' },
          ],
        },
        {
          label: 'Integrations',
          items: [
            { label: 'Todoist Setup', slug: 'guides/todoist-setup' },
            { label: 'Microsoft To-Do', slug: 'guides/mstodo-setup' },
          ],
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
