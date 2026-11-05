import assert from 'node:assert/strict'
import { test } from 'node:test'
import { registerHooks } from 'node:module'
import { existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { statSync } from 'node:fs'
const require_isDir = (url) => statSync(fileURLToPath(url)).isDirectory()

// Resolve '@/…' to the source tree (.ts or /index.ts), like Vite does.
const hooks = registerHooks({
  resolve(specifier, context, next) {
    const base = specifier.startsWith('@/')
      ? new URL(`../src/${specifier.slice(2)}`, import.meta.url)
      : specifier.startsWith('.') && context.parentURL?.includes('/src/')
        ? new URL(specifier, context.parentURL)
        : null
    if (base) {
      for (const candidate of [base.href, `${base.href}.ts`, `${base.href}/index.ts`])
        if (existsSync(fileURLToPath(candidate)) && !candidate.endsWith('/')) {
          try { if (!require_isDir(candidate)) return next(candidate, context) } catch { /* keep looking */ }
        }
    }
    return next(specifier, context)
  },
})
const { referencesOf } = await import('../src/views/admin/realm-config/manifestSelection.ts')
hooks.deregister()

// FD-05 — the selective export must pull in what nested credentials and terminal slots use.
const keys = (refs) => refs.map((r) => `${r.section}:${r.key}`).sort()

test('a service account credential references its apps and scopes', () => {
  const sa = { AccountName: 'sync', Credentials: [{ Apps: ['crm'], Scopes: ['crm-api'] }] }
  assert.deepEqual(keys(referencesOf('serviceAccounts', sa, { includeUsers: false })), ['apps:crm', 'scopes:crm-api'])
})

test('a position terminal references its apps, scopes and served positions', () => {
  const position = { AccountName: 'gate', Terminals: [{ Apps: ['door'], Scopes: ['door-api'], AllowedPositions: ['gate-b'] }] }
  assert.deepEqual(keys(referencesOf('positions', position, { includeUsers: false })),
    ['apps:door', 'positions:gate-b', 'scopes:door-api'])
})
