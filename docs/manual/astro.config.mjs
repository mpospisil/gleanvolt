// The user manual (#329): Starlight, one folder per language under src/content/docs.
//
// MANUAL_BASE is where this build is served, e.g. /gleanvolt/v1.4/ or /gleanvolt/next/ on GitHub Pages;
// docs.yml sets it per version. Locally it is /, for `npm run dev`.
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

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
      sidebar: [
        { label: 'Welcome', translations: { cs: 'Vítejte' }, slug: 'index' },
        {
          label: 'Installation',
          translations: { cs: 'Instalace' },
          items: [
            { slug: 'installation/choosing' },
            { slug: 'installation/preparing-devices' },
            { slug: 'installation/linux' },
            { slug: 'installation/windows' },
            { slug: 'installation/docker' },
            { slug: 'installation/nas' },
            { slug: 'installation/updating' },
          ],
        },
        {
          label: 'Setting up',
          translations: { cs: 'Nastavení' },
          items: [
            { slug: 'setting-up/setup-wizard' },
            { slug: 'setting-up/finding-devices' },
            { slug: 'setting-up/solar-forecast' },
            { slug: 'setting-up/weather' },
            { slug: 'setting-up/grid-tariff' },
            { slug: 'setting-up/car' },
            { slug: 'setting-up/settings' },
            { slug: 'setting-up/home-assistant' },
          ],
        },
        {
          label: 'Using Gleanvolt',
          translations: { cs: 'Používání' },
          items: [
            { slug: 'using/overview' },
            { slug: 'using/charging-plan' },
            {
              label: 'Charging modes',
              translations: { cs: 'Režimy nabíjení' },
              items: [
                { slug: 'using/modes/choosing' },
                { slug: 'using/modes/solar' },
                { slug: 'using/modes/forecasted' },
                { slug: 'using/modes/solar-grid' },
                { slug: 'using/modes/fast' },
                { slug: 'using/modes/targeted' },
              ],
            },
            { slug: 'using/battery-hold' },
            { slug: 'using/sessions' },
            { slug: 'using/energy' },
            { slug: 'using/forecast' },
          ],
        },
        {
          label: 'How it works',
          translations: { cs: 'Jak to funguje' },
          items: [{ slug: 'how-it-works/basics' }, { slug: 'how-it-works/unmetered-phase' }],
        },
        { slug: 'troubleshooting' },
        { slug: 'privacy' },
        { slug: 'glossary' },
      ],
    }),
  ],
});
