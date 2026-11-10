// The manual's screenshots (#329).
//
//   npm run screenshots          regenerate src/assets/screenshots/*.png and fingerprints.json
//   npm run screenshots:check    fail if a picture no longer shows what the UI renders
//
// tests/Gleanvolt.Web.Screenshots renders each page through bUnit from a fixed fixture and writes it as
// HTML; this script opens each file in headless Chrome and saves a PNG. No app, no devices and no
// Solcast call are involved, so a picture changes only when the page it shows does.
//
// The check compares what each picture is made of -- the page's HTML, its framing, the stylesheet and
// the chart script --
// with fingerprints.json, not the pixels: two Chrome builds anti-alias text differently, enough that a
// tolerance wide enough for that would also pass a changed word. Chrome is then needed only to make
// the pictures, and the check is exact.
//
// Chrome is the one already installed (CHROME_PATH, or the stable channel's usual place): puppeteer-core
// does not download one.

import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer-core';

const manual = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repository = resolve(manual, '..', '..');
const wwwroot = join(repository, 'src', 'Gleanvolt.Web', 'wwwroot');
const committed = join(manual, 'src', 'assets', 'screenshots');
const fingerprintsFile = join(committed, 'fingerprints.json');
const check = process.argv.includes('--check');

const html = mkdtempSync(join(tmpdir(), 'gleanvolt-screenshots-html-'));

console.log('Rendering the pages...');
execFileSync('dotnet', ['test', join(repository, 'tests', 'Gleanvolt.Web.Screenshots'), '--configuration', 'Release', '--nologo', '--verbosity', 'quiet'], {
  stdio: 'inherit',
  env: { ...process.env, GLEANVOLT_SCREENSHOTS: html },
});

const names = readdirSync(html).filter((file) => file.endsWith('.html')).map((file) => file.slice(0, -5)).sort();
if (names.length === 0) {
  throw new Error(`The screenshot tests wrote nothing into ${html}.`);
}

// What a picture is made of. The page's link to wwwroot is a path on this machine, so it is taken out;
// the stylesheet's content goes in instead, since a CSS change changes every picture.
// Line endings are normalised, so a Windows checkout agrees with CI.
const text = (path) => readFileSync(path, 'utf8').replaceAll('\r\n', '\n');
const styles = text(join(wwwroot, 'css', 'site.css'));
const charts = text(join(wwwroot, 'js', 'charts.js'));
const fingerprint = (name) => {
  const page = text(join(html, `${name}.html`)).replaceAll(pathToFileURL(wwwroot + '/').href, 'wwwroot/');
  return createHash('sha256')
    .update(page)
    .update(text(join(html, `${name}.json`)))
    .update(styles)
    .update(charts)
    .digest('hex');
};
const now = Object.fromEntries(names.map((name) => [name, fingerprint(name)]));

if (check) {
  const before = existsSync(fingerprintsFile) ? JSON.parse(readFileSync(fingerprintsFile, 'utf8')) : {};
  const problems = [];
  for (const name of new Set([...Object.keys(before), ...names])) {
    if (!(name in now)) {
      problems.push(`${name}.png: committed, but no test renders it any more`);
    } else if (!(name in before)) {
      problems.push(`${name}.png: rendered, but not committed`);
    } else if (before[name] !== now[name]) {
      problems.push(`${name}.png: the page it shows has changed`);
    } else if (!existsSync(join(committed, `${name}.png`))) {
      problems.push(`${name}.png: missing`);
    }
  }

  rmSync(html, { recursive: true, force: true });
  if (problems.length > 0) {
    console.error('\nThe screenshots no longer show the UI:');
    for (const problem of problems) {
      console.error(`  ${problem}`);
    }
    console.error('\nRun "npm run screenshots" in docs/manual and commit the result.');
    process.exit(1);
  }

  console.log('The screenshots show the UI as it is.');
  process.exit(0);
}

mkdirSync(committed, { recursive: true });
const browser = await puppeteer.launch({
  ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : { channel: 'chrome' }),
  // Dates in the European order, as most of the manual's readers see them: Chrome formats a date input
  // in its process's locale, whatever the page says.
  env: { ...process.env, LANG: 'en_GB.UTF-8', LANGUAGE: 'en_GB', LC_ALL: 'en_GB.UTF-8' },
  args: ['--lang=en-GB', '--no-sandbox', '--font-render-hinting=none', '--force-color-profile=srgb', '--hide-scrollbars'],
});

try {
  for (const name of names) {
    const { width, selector, height } = JSON.parse(readFileSync(join(html, `${name}.json`), 'utf8'));
    const page = await browser.newPage();
    await page.setViewport({ width, height: 800, deviceScaleFactor: 1 });
    await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }]);
    await page.goto(pathToFileURL(join(html, `${name}.html`)).href, { waitUntil: 'load' });
    await page.evaluate(() => document.fonts.ready);
    // The charts draw on load, and uPlot sizes itself to its box on the next frame.
    await page.evaluate(() => new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(done))));

    const path = join(committed, `${name}.png`);
    if (selector) {
      const element = await page.$(selector);
      if (!element) {
        throw new Error(`${name}: nothing matches ${selector}.`);
      }
      // A dialog is closed until its script opens it, as the page's own dialog.js would.
      await element.evaluate((node) => {
        if (node instanceof HTMLDialogElement && !node.open) {
          node.showModal();
        }
      });
      const box = await element.boundingBox();
      if (height && box.height > height) {
        await page.screenshot({ path, captureBeyondViewport: true, clip: { x: box.x, y: box.y, width: box.width, height } });
      } else {
        await element.screenshot({ path });
      }
    } else {
      await page.screenshot({ path, fullPage: true });
    }

    await page.close();
    console.log(`  ${name}.png`);
  }
} finally {
  await browser.close();
  rmSync(html, { recursive: true, force: true });
}

// A picture whose test is gone is a picture of nothing.
for (const file of readdirSync(committed).filter((file) => file.endsWith('.png'))) {
  if (!names.includes(file.slice(0, -4))) {
    rmSync(join(committed, file));
    console.log(`  removed ${file}`);
  }
}

writeFileSync(fingerprintsFile, JSON.stringify(now, null, 2) + '\n');
