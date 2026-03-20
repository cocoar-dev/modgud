#!/usr/bin/env node

/**
 * Exports the VitePress documentation as a single Markdown file.
 *
 * Usage:  node scripts/export-md.mjs [output-path]
 * Default: ./dist/cocoar-auth-docs.md
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const OUTPUT = process.argv[2] || path.join(ROOT, 'dist', 'cocoar-auth-docs.md');

// ─── Document structure (matches VitePress sidebar) ─────────────────────────

const structure = [
  {
    part: 'Concepts',
    pages: [
      { file: 'concepts/glossary.md', title: 'Glossary' },
      { file: 'concepts/realms.md', title: 'Realms' },
      { file: 'concepts/authentication.md', title: 'Authentication Model' },
      { file: 'concepts/oauth.md', title: 'OAuth & OIDC' },
      { file: 'concepts/tokens.md', title: 'Tokens & Sessions' },
    ],
  },
  {
    part: 'User Guide',
    pages: [
      { file: 'user-guide/first-setup.md', title: 'First-Time Setup' },
      { file: 'user-guide/realms.md', title: 'Managing Realms' },
      { file: 'user-guide/realm-setup.md', title: 'Realm Setup Flow' },
      { file: 'user-guide/users.md', title: 'Managing Users' },
      { file: 'user-guide/roles.md', title: 'Managing Roles' },
      { file: 'user-guide/clients.md', title: 'Registering Clients' },
      { file: 'user-guide/scopes.md', title: 'Scopes & Permissions' },
      { file: 'user-guide/api-resources.md', title: 'APIs' },
      { file: 'user-guide/client-flows.md', title: 'Client Flows' },
      { file: 'user-guide/two-factor.md', title: 'Two-Factor Authentication' },
      { file: 'user-guide/sessions.md', title: 'Session Management' },
    ],
  },
  {
    part: 'Developer Guide',
    pages: [
      { file: 'guide/overview.md', title: 'Overview' },
      { file: 'guide/getting-started.md', title: 'Getting Started' },
      { file: 'guide/architecture.md', title: 'Clean Architecture' },
      { file: 'guide/cqrs-event-sourcing.md', title: 'CQRS & Event Sourcing' },
      { file: 'guide/realms.md', title: 'Multi-Tenancy / Realms' },
      { file: 'guide/auth-cookies.md', title: 'Cookie-Based Auth' },
      { file: 'guide/two-factor.md', title: 'Two-Factor Authentication' },
      { file: 'guide/oauth.md', title: 'OAuth / OpenID Connect' },
      { file: 'guide/frontend.md', title: 'Vue Frontend' },
      { file: 'guide/frontend-realms.md', title: 'Realm-Aware SPA' },
      { file: 'guide/deployment.md', title: 'Docker & Deployment' },
      { file: 'guide/database.md', title: 'Database & Migrations' },
    ],
  },
  {
    part: 'API Reference',
    pages: [
      { file: 'reference/auth-api.md', title: 'Auth Endpoints' },
      { file: 'reference/admin-api.md', title: 'Admin Endpoints' },
      { file: 'reference/realm-api.md', title: 'Realm Endpoints' },
      { file: 'reference/oauth-api.md', title: 'OAuth Endpoints' },
    ],
  },
];

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
  parts.push(`# Cocoar.Auth Documentation`);
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
console.log(`Exporting Cocoar.Auth documentation to Markdown...\n`);
console.log(`  Sections: ${structure.length}`);
console.log(`  Pages:    ${pageCount}`);

const doc = buildDocument();

fs.mkdirSync(path.dirname(OUTPUT), { recursive: true });
fs.writeFileSync(OUTPUT, doc, 'utf-8');

const sizeKb = (Buffer.byteLength(doc, 'utf-8') / 1024).toFixed(0);
console.log(`\n  Output: ${OUTPUT} (${sizeKb} KB)\n`);
