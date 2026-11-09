import assert from 'node:assert/strict'
import { beforeEach, afterEach, test } from 'node:test'
import { registerHooks } from 'node:module'
import { createPinia, setActivePinia } from 'pinia'

// B3-F04 — the group editor started from a list cached once per SPA session; saving it
// later restored members and roles another admin had removed meanwhile.
const hooks = registerHooks({
  resolve(specifier, context, next) {
    if (specifier === '@/composables/useHttpClient')
      return next(new URL('../src/composables/useHttpClient.ts', import.meta.url).href, context)
    return next(specifier, context)
  },
})
const { useGroupStore } = await import('../src/stores/group.store.ts')
hooks.deregister()

const response = (status, body) => new Response(JSON.stringify(body), {
  status, headers: { 'Content-Type': 'application/json' },
})
let descriptors
beforeEach(() => {
  setActivePinia(createPinia())
  descriptors = ['window'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)])
  Object.defineProperty(globalThis, 'window', { configurable: true,
    value: { location: { pathname: '/admin/groups', search: '', href: '/admin/groups' } } })
})
afterEach(() => {
  for (const [key, descriptor] of descriptors) {
    if (descriptor) Object.defineProperty(globalThis, key, descriptor)
    else delete globalThis[key]
  }
})

test('fetching a group replaces the stale cached member and role lists', async t => {
  let current = { MemberIds: ['u1', 'u2'], RoleIds: ['r1', 'r2'] }
  t.mock.method(globalThis, 'fetch', async url => String(url).endsWith('/api/group')
    ? response(200, [{ Id: 'g1', Name: 'Ops', ...structuredClone(current) }])
    : response(200, { Id: 'g1', Name: 'Ops', ...structuredClone(current) }))
  const sut = useGroupStore()
  await sut.initialize()
  current = { MemberIds: ['u1'], RoleIds: ['r1'] } // another admin removed u2 and r2

  const fresh = await sut.fetchGroup('g1')

  assert.deepEqual(fresh.MemberIds, ['u1'])
  assert.deepEqual(fresh.RoleIds, ['r1'])
  assert.deepEqual(sut.groups.find(g => g.Id === 'g1').MemberIds, ['u1'])
})
