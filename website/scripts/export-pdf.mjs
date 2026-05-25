#!/usr/bin/env node

/**
 * Exports the VitePress documentation as a single, professionally-formatted PDF.
 *
 * Usage:  node scripts/export-pdf.mjs [output-path]
 * Default: ./dist/modgud-docs.pdf
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';
import puppeteer from 'puppeteer-core';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const OUTPUT = process.argv[2] || path.join(ROOT, 'dist', 'modgud-docs.pdf');

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

// ─── Markdown setup ─────────────────────────────────────────────────────────

const md = new MarkdownIt({
  html: true,
  linkify: true,
  typographer: true,
  highlight(str, lang) {
    if (lang && hljs.getLanguage(lang)) {
      try {
        return `<pre class="hljs"><code>${hljs.highlight(str, { language: lang }).value}</code></pre>`;
      } catch (_) { /* fall through */ }
    }
    return `<pre class="hljs"><code>${md.utils.escapeHtml(str)}</code></pre>`;
  },
});

// ─── Markdown pre-processing ────────────────────────────────────────────────

function slugify(text) {
  return text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
}

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
      const title = openMatch[2]?.trim() ||
        { tip: 'Tip', warning: 'Warning', info: 'Info', danger: 'Danger', details: 'Details' }[type] ||
        type.charAt(0).toUpperCase() + type.slice(1);
      stack.push(type);
      result.push(`<div class="callout callout-${type}"><p class="callout-title">${title}</p>`);
      result.push('');
      continue;
    }
    if (line.trim() === ':::' && stack.length > 0) {
      stack.pop();
      result.push('');
      result.push('</div>');
      continue;
    }
    result.push(line);
  }

  return result.join('\n');
}

function rewriteInternalLinks(content) {
  return content.replace(/\[([^\]]+)\]\(\/([^)#]+)(#[^)]+)?\)/g, (_, text, linkPath, anchor) => {
    const slug = slugify(linkPath.replace(/\//g, '-').replace(/\.md$/, ''));
    return `[${text}](#${slug}${anchor || ''})`;
  });
}

function processMarkdown(filePath) {
  let content = fs.readFileSync(path.join(ROOT, filePath), 'utf-8');
  content = stripFrontmatter(content);
  content = convertContainers(content);
  content = rewriteInternalLinks(content);
  return content;
}

// ─── HTML generation ────────────────────────────────────────────────────────

function generateCoverPage() {
  return `
    <div class="cover">
      <div class="cover-content">
        <div class="cover-badge">Documentation</div>
        <h1 class="cover-title">Modgud</h1>
        <p class="cover-subtitle">Multi-tenant Identity Provider</p>
        <div class="cover-meta">
          <span class="cover-date">${new Date().toLocaleDateString('en-US', { year: 'numeric', month: 'long' })}</span>
        </div>
      </div>
      <div class="cover-footer">
        <p>Apache-2.0 License</p>
      </div>
    </div>`;
}

function generateToc() {
  let html = '<div class="toc"><h1 class="toc-title">Table of Contents</h1>';
  for (const section of structure) {
    html += `<div class="toc-part">${section.part}</div>`;
    html += '<ul class="toc-list">';
    for (const page of section.pages) {
      const slug = slugify(page.file.replace(/\//g, '-').replace(/\.md$/, ''));
      html += `<li><a href="#${slug}">${page.title}</a></li>`;
    }
    html += '</ul>';
  }
  html += '</div>';
  return html;
}

function generateBody() {
  let html = '';
  for (const section of structure) {
    html += `<div class="part-divider"><span>${section.part}</span></div>`;
    for (const page of section.pages) {
      const slug = slugify(page.file.replace(/\//g, '-').replace(/\.md$/, ''));
      const content = processMarkdown(page.file);
      const rendered = md.render(content);
      html += `<section class="chapter" id="${slug}">${rendered}</section>`;
    }
  }
  return html;
}

// ─── CSS ────────────────────────────────────────────────────────────────────

const CSS = `
@page { size: A4; margin: 22mm 18mm 22mm 18mm; }
* { box-sizing: border-box; }
html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif;
  font-size: 9.5pt; line-height: 1.65; color: #1a1a2e; margin: 0; padding: 0;
}
.cover { page-break-after: always; display: flex; flex-direction: column; justify-content: center; align-items: center; min-height: 100vh; text-align: center; position: relative; }
.cover-content { margin-top: -80px; }
.cover-badge { display: inline-block; font-size: 10pt; font-weight: 600; letter-spacing: 2px; text-transform: uppercase; color: #5672cd; border: 2px solid #5672cd; border-radius: 4px; padding: 4px 16px; margin-bottom: 24px; }
.cover-title { font-size: 36pt; font-weight: 700; color: #1a1a2e; margin: 0 0 12px 0; letter-spacing: -0.5px; }
.cover-subtitle { font-size: 14pt; color: #64748b; font-weight: 400; margin: 0 0 32px 0; }
.cover-meta { font-size: 11pt; color: #94a3b8; }
.cover-footer { position: absolute; bottom: 0; font-size: 8.5pt; color: #94a3b8; }
.toc { page-break-after: always; }
.toc-title { font-size: 22pt; font-weight: 700; color: #1a1a2e; margin: 0 0 28px 0; padding-bottom: 12px; border-bottom: 2px solid #e2e8f0; }
.toc-part { font-size: 10.5pt; font-weight: 700; color: #5672cd; text-transform: uppercase; letter-spacing: 1px; margin: 20px 0 6px 0; }
.toc-list { list-style: none; padding: 0; margin: 0; }
.toc-list li { margin: 0; padding: 4px 0 4px 16px; border-bottom: 1px dotted #e2e8f0; }
.toc-list a { color: #1a1a2e; text-decoration: none; font-size: 9.5pt; }
.part-divider { page-break-before: always; display: flex; align-items: center; justify-content: center; min-height: 35vh; text-align: center; }
.part-divider span { font-size: 28pt; font-weight: 700; color: #1a1a2e; letter-spacing: -0.3px; position: relative; }
.part-divider span::after { content: ''; display: block; width: 60px; height: 3px; background: #5672cd; margin: 16px auto 0; border-radius: 2px; }
.chapter { page-break-before: always; }
h1 { font-size: 20pt; font-weight: 700; color: #1a1a2e; margin: 0 0 16px 0; padding-bottom: 8px; border-bottom: 2px solid #e2e8f0; }
h2 { font-size: 14pt; font-weight: 700; color: #1a1a2e; margin: 28px 0 10px 0; padding-bottom: 5px; border-bottom: 1px solid #f1f5f9; }
h3 { font-size: 11.5pt; font-weight: 600; color: #334155; margin: 22px 0 8px 0; }
h4 { font-size: 10pt; font-weight: 600; color: #475569; margin: 16px 0 6px 0; }
p { margin: 8px 0; orphans: 3; widows: 3; }
a { color: #5672cd; text-decoration: none; }
code { font-family: 'Cascadia Code', 'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-size: 8.5pt; background: #f1f5f9; border: 1px solid #e2e8f0; border-radius: 3px; padding: 1px 4px; }
pre.hljs { background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 6px; padding: 14px 16px; margin: 12px 0; overflow-x: auto; break-inside: avoid; }
pre.hljs code { background: none; border: none; padding: 0; font-size: 8pt; line-height: 1.55; color: #1e293b; }
.hljs-keyword { color: #8250df; font-weight: 600; } .hljs-built_in { color: #8250df; } .hljs-type { color: #0550ae; } .hljs-title { color: #0550ae; } .hljs-title.class_ { color: #0550ae; } .hljs-title.function_ { color: #6639ba; } .hljs-string { color: #0a3069; } .hljs-number { color: #0550ae; } .hljs-literal { color: #0550ae; } .hljs-comment { color: #6e7781; font-style: italic; } .hljs-attr { color: #0550ae; } .hljs-attribute { color: #0550ae; } .hljs-meta { color: #6e7781; } .hljs-params { color: #24292f; } .hljs-property { color: #0550ae; } .hljs-variable { color: #953800; }
table { width: 100%; border-collapse: collapse; margin: 12px 0; font-size: 8.5pt; break-inside: avoid; }
th { background: #f1f5f9; font-weight: 600; text-align: left; padding: 8px 10px; border: 1px solid #e2e8f0; }
td { padding: 7px 10px; border: 1px solid #e2e8f0; vertical-align: top; }
tr:nth-child(even) td { background: #f8fafc; }
ul, ol { margin: 8px 0; padding-left: 24px; } li { margin: 3px 0; } li > p { margin: 2px 0; }
blockquote { border-left: 3px solid #e2e8f0; margin: 12px 0; padding: 4px 16px; color: #64748b; }
hr { border: none; border-top: 1px solid #e2e8f0; margin: 24px 0; }
.callout { border-left: 4px solid; border-radius: 0 6px 6px 0; padding: 12px 16px; margin: 14px 0; break-inside: avoid; }
.callout p { margin: 4px 0; }
.callout-title { font-weight: 700; font-size: 9pt; text-transform: uppercase; letter-spacing: 0.5px; margin: 0 0 6px 0 !important; }
.callout-tip { border-color: #10b981; background: #ecfdf5; } .callout-tip .callout-title { color: #059669; }
.callout-warning { border-color: #f59e0b; background: #fffbeb; } .callout-warning .callout-title { color: #d97706; }
.callout-info { border-color: #3b82f6; background: #eff6ff; } .callout-info .callout-title { color: #2563eb; }
.callout-danger { border-color: #ef4444; background: #fef2f2; } .callout-danger .callout-title { color: #dc2626; }
img { max-width: 100%; height: auto; }
strong { font-weight: 600; }
h1, h2, h3, h4 { page-break-after: avoid; }
pre, table, .callout { page-break-inside: avoid; }
`;

// ─── HTML assembly ──────────────────────────────────────────────────────────

function buildHtml() {
  const cover = generateCoverPage();
  const toc = generateToc();
  const body = generateBody();

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Modgud Documentation</title>
  <style>${CSS}</style>
</head>
<body>
  ${cover}
  ${toc}
  ${body}
</body>
</html>`;
}

// ─── PDF generation ─────────────────────────────────────────────────────────

function findChrome() {
  const candidates = [
    process.env.CHROME_PATH,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium-browser',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ].filter(Boolean);

  for (const p of candidates) {
    if (fs.existsSync(p)) return p;
  }
  throw new Error('Chrome not found. Set CHROME_PATH environment variable.');
}

async function generatePdf(html) {
  const chromePath = findChrome();
  console.log(`  Chrome: ${chromePath}`);

  const browser = await puppeteer.launch({
    executablePath: chromePath,
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox'],
  });

  const page = await browser.newPage();
  await page.setContent(html, { waitUntil: 'networkidle0' });

  fs.mkdirSync(path.dirname(OUTPUT), { recursive: true });

  await page.pdf({
    path: OUTPUT,
    format: 'A4',
    margin: { top: '22mm', right: '18mm', bottom: '22mm', left: '18mm' },
    printBackground: true,
    displayHeaderFooter: true,
    headerTemplate: '<span></span>',
    footerTemplate: `
      <div style="width: 100%; text-align: center; font-size: 8pt; color: #94a3b8; font-family: sans-serif;">
        <span>Modgud</span>
        <span style="margin: 0 8px;">&middot;</span>
        <span class="pageNumber"></span> / <span class="totalPages"></span>
      </div>`,
  });

  await browser.close();
  return OUTPUT;
}

// ─── Main ───────────────────────────────────────────────────────────────────

async function main() {
  console.log('Exporting Modgud documentation to PDF...\n');

  const pageCount = structure.reduce((sum, s) => sum + s.pages.length, 0);
  console.log(`  Sections: ${structure.length}`);
  console.log(`  Pages:    ${pageCount}`);

  console.log('\n  Building HTML...');
  const html = buildHtml();

  console.log('  Generating PDF...');
  const outputPath = await generatePdf(html);

  const stats = fs.statSync(outputPath);
  const sizeMb = (stats.size / 1024 / 1024).toFixed(1);
  console.log(`\n  Output: ${outputPath} (${sizeMb} MB)\n`);
}

main().catch((err) => {
  console.error('\nExport failed:', err.message);
  process.exit(1);
});
