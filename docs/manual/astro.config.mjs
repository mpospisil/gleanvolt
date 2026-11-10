// The user manual (#329): Starlight, one folder per language under src/content/docs.
//
// MANUAL_BASE is where this build is served, e.g. /gleanvolt/v1.4/ or /gleanvolt/next/ on GitHub Pages;
// docs.yml sets it per version. Locally it is /, for `npm run dev`.
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

const base = process.env.MANUAL_BASE ?? '/';

export default defineConfig({
  site: process.env.MANUAL_SITE ?? 'https://mpospisil.github.io',
  base,
  trailingSlash: 'always',
  redirects: { '/': `${base.replace(/\/$/, '')}/en/` },
  integrations: [
    starlight({
      title: 'Gleanvolt',
      description: 'Charge your electric car from your own solar panels.',
      defaultLocale: 'en',
      locales: {
        en: { label: 'English', lang: 'en' },
        cs: { label: 'Čeština', lang: 'cs' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/mpospisil/gleanvolt' }],
      plugins: [starlightLinksValidator({ errorOnRelativeLinks: false })],
      sidebar: [
        { label: 'Welcome', translations: { cs: 'Vítejte' }, slug: 'index' },
        {
          label: 'Setting up',
          translations: { cs: 'Nastavení' },
          items: [{ slug: 'setting-up/setup-wizard' }],
        },
      ],
    }),
  ],
});
