// The manual's screenshots (#329).
//
//   npm run screenshots          regenerate src/assets/screenshots/*.png
//   npm run screenshots:check    regenerate into a temporary folder and fail if any differs
//
// tests/Gleanvolt.Web.Screenshots renders each page through bUnit from a fixed fixture and writes it as
// HTML; this script opens each file in headless Chrome and saves a PNG. No app, no devices and no
// Solcast call are involved, so a picture changes only when the page it shows does.
//
// Chrome is the one already installed (CHROME_PATH, or the stable channel's usual place): puppeteer-core
// does not download one.

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync, copyFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer-core';
import pixelmatch from 'pixelmatch';
import { PNG } from 'pngjs';

const manual = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repository = resolve(manual, '..', '..');
const committed = join(manual, 'src', 'assets', 'screenshots');
const check = process.argv.includes('--check');

// Anti-aliasing differs a little between Chrome builds; a changed page differs by far more than this.
const tolerance = 0.002;

const html = mkdtempSync(join(tmpdir(), 'gleanvolt-screenshots-html-'));
const target = check ? mkdtempSync(join(tmpdir(), 'gleanvolt-screenshots-png-')) : committed;

console.log('Rendering the pages...');
execFileSync('dotnet', ['test', join(repository, 'tests', 'Gleanvolt.Web.Screenshots'), '--configuration', 'Release', '--nologo', '--verbosity', 'quiet'], {
  stdio: 'inherit',
  env: { ...process.env, GLEANVOLT_SCREENSHOTS: html },
});

const names = readdirSync(html).filter((file) => file.endsWith('.html')).map((file) => file.slice(0, -5)).sort();
if (names.length === 0) {
  throw new Error(`The screenshot tests wrote nothing into ${html}.`);
}

mkdirSync(target, { recursive: true });
const browser = await puppeteer.launch({
  ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : { channel: 'chrome' }),
  args: ['--no-sandbox', '--font-render-hinting=none', '--force-color-profile=srgb', '--hide-scrollbars'],
});

try {
  for (const name of names) {
    const { width, selector } = JSON.parse(readFileSync(join(html, `${name}.json`), 'utf8'));
    const page = await browser.newPage();
    await page.setViewport({ width, height: 800, deviceScaleFactor: 1 });
    await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }]);
    await page.goto(pathToFileURL(join(html, `${name}.html`)).href, { waitUntil: 'load' });
    await page.evaluate(() => document.fonts.ready);

    const path = join(target, `${name}.png`);
    if (selector) {
      const element = await page.$(selector);
      if (!element) {
        throw new Error(`${name}: nothing matches ${selector}.`);
      }
      await element.screenshot({ path });
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

if (!check) {
  // A picture whose test is gone is a picture of nothing.
  for (const file of readdirSync(committed).filter((file) => file.endsWith('.png'))) {
    if (!names.includes(file.slice(0, -4))) {
      rmSync(join(committed, file));
      console.log(`  removed ${file}`);
    }
  }
  process.exit(0);
}

const problems = [];
const existing = existsSync(committed) ? readdirSync(committed).filter((file) => file.endsWith('.png')) : [];
for (const file of existing) {
  if (!names.includes(file.slice(0, -4))) {
    problems.push(`${file}: committed, but no test renders it any more`);
  }
}

for (const name of names) {
  const file = `${name}.png`;
  if (!existing.includes(file)) {
    problems.push(`${file}: rendered, but not committed`);
    continue;
  }

  const before = PNG.sync.read(readFileSync(join(committed, file)));
  const after = PNG.sync.read(readFileSync(join(target, file)));
  if (before.width !== after.width || before.height !== after.height) {
    problems.push(`${file}: ${before.width}x${before.height} committed, ${after.width}x${after.height} now`);
    continue;
  }

  const diff = new PNG({ width: after.width, height: after.height });
  const changed = pixelmatch(before.data, after.data, diff.data, after.width, after.height, { threshold: 0.1 });
  const share = changed / (after.width * after.height);
  if (share > tolerance) {
    problems.push(`${file}: ${(share * 100).toFixed(2)} % of the pixels differ`);
    writeFileSync(join(target, `${name}.diff.png`), PNG.sync.write(diff));
  }
}

if (problems.length > 0) {
  // Left where CI can upload them, so the fix is to download and commit rather than to rerun locally.
  const out = process.env.SCREENSHOTS_OUT ?? join(manual, 'screenshots-changed');
  mkdirSync(out, { recursive: true });
  for (const file of readdirSync(target)) {
    copyFileSync(join(target, file), join(out, file));
  }

  console.error('\nThe screenshots no longer match the UI:');
  for (const problem of problems) {
    console.error(`  ${problem}`);
  }
  console.error(`\nThe new pictures are in ${out}. Run "npm run screenshots" in docs/manual and commit the result.`);
  process.exit(1);
}

console.log('The screenshots match the UI.');
