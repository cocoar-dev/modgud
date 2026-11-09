import assert from 'node:assert/strict'
import { beforeEach, afterEach, test } from 'node:test'
import { registerHooks } from 'node:module'
import { createPinia, setActivePinia } from 'pinia'

// AREG-07 — minting succeeded, but the list refresh afterwards failed (for example
// invite-code:write without invite-code:read): the store threw, the modal showed an
// error and the one-time plaintext codes were gone — the server never returns them again.
const hubUrl = 'data:text/javascript,' + encodeURIComponent(`
  export function useSignalR() {
    return { stream() { return { subscribe() {} }; }, runOnReconnect() {} };
  }
`)
const hooks = registerHooks({
  resolve(specifier, context, next) {
    if (specifier === '@/composables/useSignalR')
      return { url: hubUrl, shortCircuit: true }
    if (specifier === '@/composables/useHttpClient')
      return next(new URL('../src/composables/useHttpClient.ts', import.meta.url).href, context)
    return next(specifier, context)
  },
})
const { useInviteCodeStore } = await import('../src/stores/inviteCode.store.ts')
hooks.deregister()

const response = (status, body) => new Response(JSON.stringify(body), {
  status, headers: { 'Content-Type': 'application/json' },
})

let descriptors
beforeEach(() => {
  setActivePinia(createPinia())
  descriptors = ['window', 'sessionStorage'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)])
  Object.defineProperty(globalThis, 'window', { configurable: true,
    value: { location: { pathname: '/admin/invite-codes', search: '', href: '/admin/invite-codes' } } })
  const storage = new Map()
  Object.defineProperty(globalThis, 'sessionStorage', { configurable: true,
    value: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) } })
})
afterEach(() => {
  for (const [key, descriptor] of descriptors) {
    if (descriptor) Object.defineProperty(globalThis, key, descriptor)
    else delete globalThis[key]
  }
})

test('a failed list refresh after minting still returns the one-time codes', async t => {
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    const method = (init?.method ?? 'GET').toUpperCase()
    if (method === 'POST') return response(200, { Codes: ['CODE-ONE', 'CODE-TWO'] })
    return response(403, { Message: 'Forbidden' })
  })
  const sut = useInviteCodeStore()

  const result = await sut.mint('app-1', { Count: 2, BoundEmail: null, ExpiresInDays: 14 })

  assert.deepEqual(result.Codes, ['CODE-ONE', 'CODE-TWO'])
  assert.equal(sut.loadFailed, true)
})
