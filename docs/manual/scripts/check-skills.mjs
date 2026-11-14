// Lints the agent skills in skills/ (#390) against the Agent Skills format Agensi lists them in, and
// checks every link they make into the manual against the built manual. Run after `astro build`.
//
// The skills link to the published manual (https://mpospisil.github.io/gleanvolt/latest/...). dist/ is
// built with the base /, so the part after /gleanvolt/latest/ is the page's path in dist/. Each skill
// tells the agent that the Czech page is the same link with /cs/ for /en/, so that page must exist too.

import { existsSync, lstatSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';

const here = dirname(fileURLToPath(import.meta.url));
const dist = resolve(here, '..', 'dist');
const skills = resolve(here, '..', '..', '..', 'skills');
const manual = 'https://mpospisil.github.io/gleanvolt/latest/';

// https://agentskills.io/specification
const allowedKeys = new Set(['name', 'description', 'license', 'compatibility', 'metadata', 'allowed-tools']);
const namePattern = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const maxBodyLines = 500;

if (!existsSync(dist)) {
  console.error('dist/ is missing: run `astro build` first.');
  process.exit(1);
}

const problems = [];
const folders = readdirSync(skills, { withFileTypes: true }).filter((entry) => entry.isDirectory());
if (folders.length === 0) {
  problems.push('skills/ has no skill folders');
}

const pageExists = (path) => existsSync(join(dist, path.replace(/#.*$/, ''), 'index.html'));

const walk = (directory, report) => {
  for (const entry of readdirSync(directory)) {
    const path = join(directory, entry);
    const where = path.slice(skills.length + 1);
    if (entry.startsWith('.')) {
      report(`${where}: hidden file (Agensi's scan rejects it)`);
    }
    const stat = lstatSync(path);
    if (stat.isSymbolicLink()) {
      report(`${where}: symlink (Agensi's scan rejects it)`);
    } else if (stat.isDirectory()) {
      walk(path, report);
    }
  }
};

for (const folder of folders) {
  const name = folder.name;
  const report = (message) => problems.push(`skills/${name}: ${message}`);
  walk(join(skills, name), (message) => problems.push(`skills/${message}`));

  const file = join(skills, name, 'SKILL.md');
  if (!existsSync(file)) {
    report('no SKILL.md');
    continue;
  }

  const text = readFileSync(file, 'utf8');
  const match = text.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (!match) {
    report('SKILL.md does not start with a --- frontmatter block');
    continue;
  }

  let front;
  try {
    front = parse(match[1]);
  } catch (error) {
    report(`frontmatter is not valid YAML: ${error.message}`);
    continue;
  }
  if (front === null || typeof front !== 'object' || Array.isArray(front)) {
    report('frontmatter is not a map');
    continue;
  }

  for (const key of Object.keys(front)) {
    if (!allowedKeys.has(key)) {
      report(`frontmatter key "${key}" is not in the Agent Skills spec (put it under metadata)`);
    }
  }

  if (typeof front.name !== 'string' || !namePattern.test(front.name) || front.name.length > 64) {
    report('name must be 1-64 lowercase letters, digits and single hyphens');
  } else if (front.name !== name) {
    report(`name "${front.name}" does not match its folder`);
  }

  if (typeof front.description !== 'string' || front.description.trim() === '') {
    report('description is missing');
  } else if (front.description.length > 1024) {
    report(`description is ${front.description.length} characters, over 1024`);
  }

  if (front.compatibility !== undefined
    && (typeof front.compatibility !== 'string' || front.compatibility.length > 500)) {
    report('compatibility must be a string of at most 500 characters');
  }

  if (front.metadata !== undefined) {
    if (front.metadata === null || typeof front.metadata !== 'object' || Array.isArray(front.metadata)) {
      report('metadata must be a map');
    } else {
      for (const [key, value] of Object.entries(front.metadata)) {
        if (typeof value !== 'string') {
          report(`metadata.${key} must be a string (quote it)`);
        }
      }
      if (typeof front.metadata.version !== 'string' || !/^\d+\.\d+\.\d+$/.test(front.metadata.version)) {
        report('metadata.version must be a version like "1.3.0", the Gleanvolt release it was checked against');
      }
    }
  }

  const body = match[2];
  if (body.trim() === '') {
    report('SKILL.md has no instructions after the frontmatter');
  }
  const lines = body.split('\n').length;
  if (lines > maxBodyLines) {
    report(`SKILL.md is ${lines} lines; keep it under ${maxBodyLines}`);
  }

  // Links into the manual, in Markdown links, bare URLs and frontmatter alike.
  for (const [url] of text.matchAll(/https:\/\/mpospisil\.github\.io\/gleanvolt\/[^\s)>"'`]*/g)) {
    if (!url.startsWith(manual)) {
      report(`${url} links to a versioned manual; link to latest/`);
      continue;
    }
    const path = url.slice(manual.length);
    if (!pageExists(path)) {
      report(`${url} is not a page in the manual`);
    } else if (path.startsWith('en/') && !pageExists(`cs/${path.slice(3)}`)) {
      report(`${url} has no Czech page at cs/${path.slice(3)}`);
    }
  }

  // Links to files inside the skill folder.
  for (const [, target] of body.matchAll(/\]\((?!https?:|mailto:|#)([^)\s]+)\)/g)) {
    if (!existsSync(join(skills, name, target.replace(/#.*$/, '')))) {
      report(`link to ${target}, which is not in the skill folder`);
    }
  }
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s) in the agent skills:`);
  for (const problem of problems) {
    console.error(`  ${problem}`);
  }
  process.exit(1);
}

console.log(`${folders.length} agent skill(s) are valid, and their manual links lead somewhere.`);
