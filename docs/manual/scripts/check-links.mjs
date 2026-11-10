// Checks every link in the built manual (#329): each page a link points to exists in dist/, and so does
// the heading its #anchor names. Run after `astro build`, as part of `npm run build`.
//
// On the built HTML rather than the Markdown, because the manual links its pages to each other with
// relative paths (it is served under a different base for every version) and starlight-links-validator
// only checks absolute ones. Here every link is checked as the browser will follow it, including the
// ones Starlight generates (sidebar, language picker, pagination).

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const dist = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const base = (process.env.MANUAL_BASE ?? '/').replace(/\/?$/, '/');

const pages = [];
const walk = (directory) => {
  for (const entry of readdirSync(directory)) {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) {
      walk(path);
    } else if (entry.endsWith('.html')) {
      pages.push(path);
    }
  }
};
walk(dist);

const decode = (text) =>
  text.replaceAll('&amp;', '&').replaceAll('&quot;', '"').replaceAll('&#39;', "'").replaceAll('&lt;', '<').replaceAll('&gt;', '>');

const ids = new Map();
const idsOf = (file) => {
  if (!ids.has(file)) {
    const html = readFileSync(file, 'utf8');
    ids.set(file, new Set([...html.matchAll(/\sid="([^"]*)"/g)].map((match) => decode(match[1]))));
  }
  return ids.get(file);
};

// The address a page is served at, under the base: dist/en/linux/index.html -> /base/en/linux/
const addressOf = (file) => {
  const path = relative(dist, file).split(sep).join('/');
  return base + path.replace(/(^|\/)index\.html$/, '$1');
};

// The file an address is served from, or null when it is outside the site.
const fileOf = (pathname) => {
  if (!pathname.startsWith(base)) {
    return null;
  }
  const path = decodeURIComponent(pathname.slice(base.length));
  if (path === '' || path.endsWith('/')) {
    return join(dist, path, 'index.html');
  }
  return path.endsWith('.html') ? join(dist, path) : join(dist, path, 'index.html');
};

const problems = [];
for (const page of pages) {
  const html = readFileSync(page, 'utf8');
  const here = new URL(addressOf(page), 'https://manual.invalid');

  for (const [, raw] of html.matchAll(/<a\s[^>]*?href="([^"]*)"/g)) {
    const href = decode(raw);
    if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('//')) {
      continue; // another site, mailto: and the like
    }

    const target = new URL(href, here);
    const file = target.pathname === here.pathname ? page : fileOf(target.pathname);
    const where = `${relative(dist, page)}: ${href}`;

    if (file === null) {
      problems.push(`${where} leaves the manual (outside ${base})`);
    } else if (!existsSync(file)) {
      problems.push(`${where} points to a page that doesn't exist`);
    } else if (target.hash && !idsOf(file).has(decodeURIComponent(target.hash.slice(1)))) {
      problems.push(`${where} points to a heading that doesn't exist`);
    }
  }
}

if (problems.length > 0) {
  console.error(`\n${problems.length} broken link(s) in the manual:`);
  for (const problem of [...new Set(problems)].sort()) {
    console.error(`  ${problem}`);
  }
  process.exit(1);
}

console.log(`All links in ${pages.length} pages lead somewhere.`);
