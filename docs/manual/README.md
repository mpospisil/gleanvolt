# The user manual

The source of the Gleanvolt user manual ([#329](https://github.com/mpospisil/gleanvolt/issues/329)): a
[Starlight](https://starlight.astro.build/) site, built by [`docs.yml`](../../.github/workflows/docs.yml)
and published to GitHub Pages — `/next/` from `main`, `/vX.Y/` and `/latest/` from each release tag.

It is written for users, not developers: one task per page, numbered steps, a screenshot for each step,
plain words. The other pages in `docs/` are the source of the facts, not part of the manual, and the
manual doesn't link to them.

## Working on it

Node 24 is needed for the manual only; nothing in the .NET build uses it.

```bash
cd docs/manual
npm ci
npm run dev        # http://localhost:4321, reloads as you edit
npm run build      # what CI runs; fails on a broken link or anchor (scripts/check-links.mjs)
```

## Pages and languages

Pages are under `src/content/docs/<language>/`, `en/` and `cs/` so far. English is the source and is
always complete. A page missing from another language shows the English one with a notice, so a new
language never blocks a release. In Czech, *import*, *export* and the mode names stay untranslated.

A new page goes into the `sidebar` in [`astro.config.mjs`](astro.config.mjs). Link pages to each other
with relative paths ending in `/`, for example `../../setting-up/setup-wizard/` from
`en/installation/linux.mdx`: every version is served under its own base, so a link starting with `/`
would leave it. The build checks every link and anchor in the result.

A file from the repository can be shown on a page without copying it, as `installation/nas.mdx` does
with the NAS compose file: `import text from '<path>?raw'` and Starlight's `<Code>`.

## Screenshots

The pictures in `src/assets/screenshots/` are generated, never taken by hand:

```bash
npm run screenshots          # regenerate them, and fingerprints.json
npm run screenshots:check    # what CI runs on a pull request: fail if a pictured page has changed
```

[`tests/Gleanvolt.Web.Screenshots`](../../tests/Gleanvolt.Web.Screenshots) renders each page through bUnit
from a fixed fixture, and [`scripts/screenshots.mjs`](scripts/screenshots.mjs) opens each in the installed
Google Chrome and saves a PNG. No inverter, charger or Solcast call is involved. The pictures are in
English and shared by every language. `fingerprints.json` records what each picture was made from (the
page's HTML and the stylesheet); the check compares that, not pixels, since every Chrome draws text a
little differently.

- **Charts** are drawn: the page's calls to `solaxCharts.*` that bUnit records are replayed in the captured
  page with the real `charts.js` and uPlot, and `charts.js` is part of the fingerprint.
- **A dialog** is opened before it is captured, when the picture's selector names a `<dialog>`.
- **Dates** in date fields are in the European order: Chrome runs with `LANG=en_GB.UTF-8`.
- **A new picture**: add a test that calls `Capture(...)` in that project, then run `npm run screenshots`.
- **A UI change** that changes a picture fails the pull request's screenshot check. Run
  `npm run screenshots` and commit the PNGs with `fingerprints.json`.
