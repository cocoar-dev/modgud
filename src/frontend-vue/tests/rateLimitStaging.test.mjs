import assert from 'node:assert/strict'
import { test } from 'node:test'
import { registerHooks } from 'node:module'

// The helper imports '@/models/realmSettings'; resolve the alias to the source file.
const hooks = registerHooks({
  resolve(specifier, context, next) {
    if (specifier === '@/models/realmSettings')
      return next(new URL('../src/models/realmSettings.ts', import.meta.url).href, context)
    return next(specifier, context)
  },
})
const { mergeRateLimitPatch } = await import('../src/views/admin/rateLimitStaging.ts')
hooks.deregister()

// F7-06 — two staged rate-limit changes must both survive.

test('changes to two different policies staged one after the other are both kept', () => {
  const first = { Policies: { Login: { Source: { Limit: 5, WindowSeconds: 60 } } } }
  const second = { Policies: { Register: { Target: { Limit: 3, WindowSeconds: 3600 } } } }
  const merged = mergeRateLimitPatch(first, second)
  assert.deepEqual(Object.keys(merged.Policies).sort(), ['Login', 'Register'])
})

test('two dimensions of one policy staged separately are both kept', () => {
  const merged = mergeRateLimitPatch(
    { Policies: { Login: { Source: { Limit: 5, WindowSeconds: 60 } } } },
    { Policies: { Login: { Target: { Limit: 2, WindowSeconds: 60 } } } })
  assert.deepEqual(Object.keys(merged.Policies.Login).sort(), ['Source', 'Target'])
})

test('a staged policy reset followed by one dimension keeps the other dimensions reset', () => {
  const merged = mergeRateLimitPatch({ Policies: { Login: null } },
    { Policies: { Login: { Source: { Limit: 9, WindowSeconds: 60 } } } })
  assert.equal(merged.Policies.Login.Target, null)
  assert.deepEqual(merged.Policies.Login.Source, { Limit: 9, WindowSeconds: 60 })
})

test('mode and allowlist follow the newer patch; absent keeps the staged value', () => {
  const merged = mergeRateLimitPatch({ Mode: 'LogOnly', SourceAllowlist: ['10.0.0.0/8'] }, { Mode: null })
  assert.equal(merged.Mode, null)
  assert.deepEqual(merged.SourceAllowlist, ['10.0.0.0/8'])
})
