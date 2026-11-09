import assert from 'node:assert/strict'
import { beforeEach, afterEach, test } from 'node:test'
import { registerHooks } from 'node:module'
import { createPinia, setActivePinia } from 'pinia'

// D.roles-F02 — the role editor started from a list cached once per SPA session; saving it
// later restored permissions another admin had revoked meanwhile.
const hooks = registerHooks({
  resolve(specifier, context, next) {
    if (specifier === '@/composables/useHttpClient')
      return next(new URL('../src/composables/useHttpClient.ts', import.meta.url).href, context)
    return next(specifier, context)
  },
})
const { useRoleStore } = await import('../src/stores/role.store.ts')
hooks.deregister()

const response = (status, body) => new Response(JSON.stringify(body), {
  status, headers: { 'Content-Type': 'application/json' },
})
let descriptors
beforeEach(() => {
  setActivePinia(createPinia())
  descriptors = ['window'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)])
  Object.defineProperty(globalThis, 'window', { configurable: true,
    value: { location: { pathname: '/admin/roles', search: '', href: '/admin/roles' } } })
})
afterEach(() => {
  for (const [key, descriptor] of descriptors) {
    if (descriptor) Object.defineProperty(globalThis, key, descriptor)
    else delete globalThis[key]
  }
})

test('fetching a role replaces the stale cached permission set', async t => {
  let current = ['read', 'write']
  t.mock.method(globalThis, 'fetch', async url => String(url).endsWith('/api/role')
    ? response(200, [{ Id: 'r1', Name: 'Editors', PermissionIds: [...current] }])
    : response(200, { Id: 'r1', Name: 'Editors', PermissionIds: [...current] }))
  const sut = useRoleStore()
  await sut.initialize()
  current = ['read'] // another admin revoked "write"

  const fresh = await sut.fetchRole('r1')

  assert.deepEqual(fresh.PermissionIds, ['read'])
  assert.deepEqual(sut.roles.find(r => r.Id === 'r1').PermissionIds, ['read'])
})
