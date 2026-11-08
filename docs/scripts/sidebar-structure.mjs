/**
 * Reads the page order of the exports from the VitePress sidebar
 * (`.vitepress/config-base.ts`), so the Markdown and PDF exports follow the
 * site instead of a hand-kept list that drifts from it.
 *
 * The sidebar is plain data — groups of `{ text, link }` items — so it is read
 * from the source text rather than by importing the config, which would load
 * VitePress plugins and run git.
 *
 * Returns `[{ part, pages: [{ file, title }] }]`, one part per sidebar group,
 * in sidebar order. Throws when the sidebar links a page that does not exist.
 */

import fs from 'fs';
import path from 'path';

export function readSidebarStructure(root) {
  const configPath = path.join(root, '.vitepress', 'config-base.ts');
  const source = fs.readFileSync(configPath, 'utf-8');
  const block = sidebarBlock(source, configPath);

  const structure = [];
  let current = null;
  for (const line of block.split('\n')) {
    const item = line.match(/\{\s*text:\s*'((?:[^'\\]|\\.)*)',\s*link:\s*'([^']+)'/);
    if (item) {
      const file = pageFile(item[2]);
      if (file && current) current.pages.push({ file, title: unescape(item[1]) });
      continue;
    }
    const group = line.match(/^\s*text:\s*'((?:[^'\\]|\\.)*)',\s*$/);
    if (group) {
      current = { part: unescape(group[1]), pages: [] };
      structure.push(current);
    }
  }

  const result = structure.filter((s) => s.pages.length > 0);
  const missing = result.flatMap((s) => s.pages).filter((p) => !fs.existsSync(path.join(root, p.file)));
  if (missing.length > 0)
    throw new Error(`The sidebar links pages that do not exist:\n${missing.map((p) => `  ${p.file}`).join('\n')}`);
  if (result.length === 0) throw new Error(`No sidebar pages found in ${configPath}`);
  return result;
}

/** The `sidebar: { … }` object literal, found by brace depth (quoted text skipped). */
function sidebarBlock(source, configPath) {
  const start = source.indexOf('sidebar: {');
  if (start < 0) throw new Error(`No sidebar in ${configPath}`);
  let depth = 0;
  let quote = null;
  for (let i = source.indexOf('{', start); i < source.length; i++) {
    const c = source[i];
    if (quote) {
      if (c === '\\') i++;
      else if (c === quote) quote = null;
      continue;
    }
    if (c === "'" || c === '"' || c === '`') quote = c;
    else if (c === '{') depth++;
    else if (c === '}' && --depth === 0) return source.slice(start, i + 1);
  }
  throw new Error(`Unterminated sidebar in ${configPath}`);
}

/** `/concepts/glossary` → `concepts/glossary.md`, `/admin/` → `admin/index.md`; external links → null. */
function pageFile(link) {
  if (!link.startsWith('/') || link.endsWith('.txt')) return null;
  const clean = link.split('#')[0].replace(/^\//, '');
  return clean === '' || clean.endsWith('/') ? `${clean}index.md` : `${clean.replace(/\.(md|html)$/, '')}.md`;
}

function unescape(text) {
  return text.replace(/\\(.)/g, '$1');
}
