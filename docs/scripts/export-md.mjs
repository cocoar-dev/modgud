#!/usr/bin/env node

/**
 * Exports the VitePress documentation as a single Markdown file.
 *
 * Usage:  node scripts/export-md.mjs [output-path]
 * Default: ./dist/modgud-docs.md
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { readSidebarStructure } from './sidebar-structure.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const OUTPUT = process.argv[2] || path.join(ROOT, 'dist', 'modgud-docs.md');

// ─── Document structure (read from the VitePress sidebar) ──────────────────

// B10-DOC-01 — the sidebar is the one list of pages; a hand-kept copy here drifted
// from the tree and made the export fail on the first page that had moved.
const structure = readSidebarStructure(ROOT);

// ─── Pre-processing ─────────────────────────────────────────────────────────

function stripFrontmatter(content) {
  return content.replace(/^---[\s\S]*?---\n*/, '');
}

function convertContainers(content) {
  const lines = content.split('\n');
  const result = [];
  const stack = [];

  for (const line of lines) {
    const openMatch = line.match(/^::: (\w+)\s*(.*)$/);
    if (openMatch) {
      const type = openMatch[1];
      const label = openMatch[2]?.trim() ||
        { tip: 'Tip', warning: 'Warning', info: 'Info', danger: 'Danger', details: 'Details' }[type] ||
        type.charAt(0).toUpperCase() + type.slice(1);
      stack.push(type);
      result.push(`> **${label}**`);
      result.push('>');
      continue;
    }
    if (line.trim() === ':::' && stack.length > 0) {
      stack.pop();
      result.push('');
      continue;
    }
    if (stack.length > 0) {
      result.push(line ? `> ${line}` : '>');
    } else {
      result.push(line);
    }
  }

  return result.join('\n');
}

function processPage(filePath) {
  let content = fs.readFileSync(path.join(ROOT, filePath), 'utf-8');
  content = stripFrontmatter(content);
  content = convertContainers(content);
  return content.trim();
}

// ─── Document assembly ──────────────────────────────────────────────────────

function buildDocument() {
  const parts = [];

  // Header
  parts.push(`# Modgud Documentation`);
  parts.push('');
  parts.push(`*Multi-tenant Identity Provider — ${new Date().toLocaleDateString('en-US', { year: 'numeric', month: 'long' })}*`);
  parts.push('');
  parts.push('---');
  parts.push('');

  // Table of contents
  parts.push('## Table of Contents');
  parts.push('');
  for (const section of structure) {
    parts.push(`**${section.part}**`);
    for (const page of section.pages) {
      parts.push(`- ${page.title}`);
    }
    parts.push('');
  }
  parts.push('---');
  parts.push('');

  // Content
  for (const section of structure) {
    parts.push(`# ${section.part}`);
    parts.push('');

    for (const page of section.pages) {
      const content = processPage(page.file);
      parts.push(content);
      parts.push('');
      parts.push('---');
      parts.push('');
    }
  }

  return parts.join('\n');
}

// ─── Main ───────────────────────────────────────────────────────────────────

const pageCount = structure.reduce((sum, s) => sum + s.pages.length, 0);
console.log(`Exporting Modgud documentation to Markdown...\n`);
console.log(`  Sections: ${structure.length}`);
console.log(`  Pages:    ${pageCount}`);

const doc = buildDocument();

fs.mkdirSync(path.dirname(OUTPUT), { recursive: true });
fs.writeFileSync(OUTPUT, doc, 'utf-8');

const sizeKb = (Buffer.byteLength(doc, 'utf-8') / 1024).toFixed(0);
console.log(`\n  Output: ${OUTPUT} (${sizeKb} KB)\n`);
