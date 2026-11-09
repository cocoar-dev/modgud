import assert from 'node:assert/strict'
import { beforeEach, afterEach, test } from 'node:test'
import { registerHooks } from 'node:module'
import { createPinia, setActivePinia } from 'pinia'

// Real Pinia stores, Vue and HTTP client; only the hub transport is isolated.
// All HTTP is intercepted in-process, never directed at a backend or container.
const hubUrl = 'data:text/javascript,' + encodeURIComponent(`
  export const hub = {
    subscriptions: [], reconnects: [],
    stream(method) { return { subscribe(observer) { hub.subscriptions.push({method, observer}); } }; },
    runOnReconnect(callback, identifier) { hub.reconnects.push({callback, identifier}); }
  };
  export function useSignalR() { return hub; }
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
const stores = [
  { name: 'applications', file: 'applications', factory: 'useApplicationsStore', list: 'apps', path: '/api/app' },
  { name: 'OAuth clients', file: 'oauthClient', factory: 'useOAuthClientStore', list: 'clients', path: '/api/admin/oauth/clients', paged: true },
  { name: 'OAuth scopes', file: 'oauthScope', factory: 'useOAuthScopeStore', list: 'scopes', path: '/api/admin/oauth/scopes', paged: true },
  { name: 'invite codes', file: 'inviteCode', factory: 'useInviteCodeStore', list: 'codes', path: '/api/admin/invite-codes', invite: true },
]
for (const config of stores) {
  config.create = (await import(`../src/stores/${config.file}.store.ts`))[config.factory]
}
const { hub } = await import(hubUrl)
const { HttpClientError } = await import('../src/composables/useHttpClient.ts')
hooks.deregister()

const tick = () => new Promise(setImmediate)
const response = (status, body) => new Response(JSON.stringify(body), {
  status, headers: { 'Content-Type': 'application/json' },
})
const payload = (config, ids) => config.paged
  ? { Items: ids.map(Id => ({ Id })), TotalCount: ids.length }
  : ids.map(Id => ({ Id }))
const ids = (store, config) => store[config.list].map(dto => dto.Id)

let descriptors
beforeEach(() => {
  setActivePinia(createPinia())
  hub.subscriptions.length = 0
  hub.reconnects.length = 0
  descriptors = ['window', 'sessionStorage'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)])
  Object.defineProperty(globalThis, 'window', { configurable: true,
    value: { location: { pathname: '/admin/apps', search: '', href: '/admin/apps' } } })
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

for (const config of stores) {
  for (const failure of ['permission', 'network', 'server']) {
    test(`${config.name}: reconnect ${failure} clears stale snapshot, marks failure and recovers without duplicate subscriptions`, async t => {
      let fail = false
      let requests = 0
      const urls = []
      t.mock.method(globalThis, 'fetch', async url => {
        requests++
        urls.push(String(url))
        if (fail && failure === 'network') throw new TypeError('Deliberate transport failure')
        return fail ? response(failure === 'permission' ? 403 : 503, { Message: 'Unavailable' })
          : response(200, payload(config, [requests === 1 ? 'stale' : 'fresh']))
      })
      const sut = config.create()
      if (config.invite) sut.setApp('selected-app')
      await sut.initialize()
      await tick()
      await sut.initialize()
      assert.deepEqual(ids(sut, config), ['stale'])
      assert.equal(sut.loaded, true)
      assert.equal(hub.subscriptions.length, 1)
      assert.equal(hub.reconnects.length, 1)
      fail = true
      await hub.reconnects[0].callback()
      await tick()
      assert.equal(requests, 2)
      assert.deepEqual(ids(sut, config), [])
      assert.equal(sut.loaded, false)
      assert.equal(sut.loadFailed, true)
      if (config.name === 'OAuth clients') assert.equal(sut.totalCount, 0)
      assert.equal(window.location.href, '/admin/apps')
      if (!config.invite) {
        hub.subscriptions[0].observer.next({ Action: 'Updated', Payload: [{ Id: 'partial' }] })
        assert.equal(sut.loadFailed, true)
        assert.equal(sut.loaded, false)
      }
      fail = false
      await hub.reconnects[0].callback()
      await tick()
      assert.equal(requests, 3)
      assert.deepEqual(ids(sut, config), ['fresh'])
      assert.equal(sut.loaded, true)
      assert.equal(sut.loadFailed, false)
      if (config.name === 'OAuth clients') assert.equal(sut.totalCount, 1)
      if (config.invite) assert.equal(sut.selectedAppId, 'selected-app')
      assert.equal(hub.subscriptions.length, 1)
      assert.equal(hub.reconnects.length, 1)
      for (const url of urls) {
        const parsed = new URL(url, 'http://isolated.invalid')
        assert.equal(parsed.pathname, config.path)
        if (config.paged) {
          assert.equal(parsed.searchParams.get('page'), '1')
          assert.equal(parsed.searchParams.get('pageSize'), '500')
        }
      }
    })
  }

  test(`${config.name}: explicit reload failure rejects; successful empty snapshot is not a failure`, async t => {
    let fail = true
    t.mock.method(globalThis, 'fetch', async () => fail ? response(503, {}) : response(200, payload(config, [])))
    const sut = config.create()
    await assert.rejects(sut.loadAll(), HttpClientError)
    assert.equal(sut.loaded, false)
    assert.equal(sut.loadFailed, true)
    if (config.invite) await assert.rejects(sut.refresh(), HttpClientError)
    else await assert.rejects(sut.initialize(), HttpClientError)
    fail = false
    assert.deepEqual(await sut.loadAll(), [])
    assert.equal(sut.loaded, true)
    assert.equal(sut.loadFailed, false)
  })

  test(`${config.name}: failed initialization retries without multiplying subscriptions`, async t => {
    let fail = true
    t.mock.method(globalThis, 'fetch', async () => fail ? response(503, {}) : response(200, payload(config, ['recovered'])))
    const sut = config.create()
    if (config.invite) {
      sut.initialize()
      await tick()
    } else {
      await assert.rejects(sut.initialize(), HttpClientError)
    }
    assert.equal(sut.loadFailed, true)
    fail = false
    await sut.initialize()
    await tick()
    assert.deepEqual(ids(sut, config), ['recovered'])
    assert.equal(sut.loaded, true)
    assert.equal(sut.loadFailed, false)
    assert.equal(hub.subscriptions.length, 1)
    assert.equal(hub.reconnects.length, 1)
  })

  test(`${config.name}: awaited mutations reject and do not pretend a partial update repairs a failed snapshot`, async t => {
    let fail = true
    t.mock.method(globalThis, 'fetch', async () => fail ? response(403, {}) : response(200, { Id: 'partial', Client: { Id: 'partial' } }))
    const sut = config.create()
    await assert.rejects(sut.loadAll(), HttpClientError)
    if (config.invite) {
      await assert.rejects(sut.mint('app', {}), HttpClientError)
      await assert.rejects(sut.revoke('app', 'id'), HttpClientError)
    } else {
      assert.equal(await sut.loadOne('id'), null) // existing detail-read contract
      await assert.rejects(sut.create({}), HttpClientError)
      await assert.rejects(sut.update('id', {}), HttpClientError)
      await assert.rejects(sut.remove('id'), HttpClientError)
      if (config.name === 'OAuth clients') {
        await assert.rejects(sut.regenerateSecret('id'), HttpClientError)
        await assert.rejects(sut.updateTerminalAccess('id', 'terminal', {}), HttpClientError)
      }
      fail = false
      await sut.create({})
    }
    assert.equal(sut.loadFailed, true)
    assert.equal(sut.loaded, false)
  })

  for (const [status, body, suffix] of [
    [401, {}, '/login'],
    [403, { RequiresSecureSetup: true }, 'stepup'],
    [403, { RequiresStepUp: true }, 'stepup'],
    [403, { RequiresReauthentication: true }, 'reauth'],
  ]) {
    test(`${config.name}: background ${status}/${suffix}/${Object.keys(body)[0] ?? 'session'} keeps the normal HTTP gate`, async t => {
      let fail = false
      t.mock.method(globalThis, 'fetch', async () => fail ? response(status, body) : response(200, payload(config, [])))
      const sut = config.create()
      await sut.initialize()
      await tick()
      fail = true
      await hub.reconnects[0].callback()
      await tick()
      // Reauthentication intentionally uses the shared (not admin-specific)
      // redirect; secure-setup/step-up retains the administration URL gate.
      const admin = suffix !== 'reauth' && config.path.startsWith('/api/admin/') ? '&for=admin' : ''
      assert.equal(window.location.href, suffix === '/login' ? '/login'
        : `/login?stepup=1${suffix === 'reauth' ? '&reauth=1' : ''}${admin}&redirect=%2Fadmin%2Fapps`)
      assert.equal(sut.loadFailed, true)
      assert.equal(sut.loaded, false)
    })
  }

  for (const oldOutcome of ['success', 'failure']) {
    test(`${config.name}: an older ${oldOutcome} cannot overwrite or invalidate a newer reload`, async t => {
      let release
      const delayed = new Promise(resolve => { release = resolve })
      let requests = 0
      t.mock.method(globalThis, 'fetch', async () => ++requests === 1 ? delayed : response(200, payload(config, ['newer'])))
      const sut = config.create()
      const older = sut.loadAll()
      const completed = oldOutcome === 'failure' ? assert.rejects(older, HttpClientError) : older
      await sut.loadAll()
      release(oldOutcome === 'success' ? response(200, payload(config, ['older'])) : response(503, {}))
      await completed
      assert.deepEqual(ids(sut, config), ['newer'])
      assert.equal(sut.loaded, true)
      assert.equal(sut.loadFailed, false)
    })
  }

  test(`${config.name}: an older success cannot heal a newer failed snapshot`, async t => {
    let release
    const delayed = new Promise(resolve => { release = resolve })
    let requests = 0
    t.mock.method(globalThis, 'fetch', async () => ++requests === 1 ? delayed : response(503, {}))
    const sut = config.create()
    const older = sut.loadAll()
    await assert.rejects(sut.loadAll(), HttpClientError)
    release(response(200, payload(config, ['older'])))
    await older
    assert.deepEqual(ids(sut, config), [])
    assert.equal(sut.loaded, false)
    assert.equal(sut.loadFailed, true)
  })
}

test('invite codes: a successful mint returns its codes and marks the failed reload', async t => {
  // AREG-07 — the plaintext codes exist only in the mint response; a failed list refresh
  // must not turn the mint into an error and lose them. The list still shows its failure.
  t.mock.method(globalThis, 'fetch', async (_url, options) =>
    options.method === 'POST' ? response(200, { Codes: ['ONE'] }) : response(503, {}))
  const sut = stores.find(config => config.invite).create()
  assert.deepEqual((await sut.mint('app', {})).Codes, ['ONE'])
  assert.equal(sut.loadFailed, true)
  assert.equal(sut.loaded, false)
})

test('invite codes: fire-and-forget initialization and event reloads handle failure and later recover', async t => {
  const config = stores.find(config => config.invite)
  let fail = true
  let requests = 0
  t.mock.method(globalThis, 'fetch', async () => {
    requests++
    return fail ? response(403, {}) : response(200, payload(config, ['recovered']))
  })
  const sut = config.create()
  assert.equal(sut.initialize(), undefined) // preserve its synchronous public contract
  await tick()
  assert.equal(sut.loadFailed, true)
  hub.subscriptions[0].observer.next({ Action: 'Created', Payload: [] })
  await tick()
  assert.equal(sut.loadFailed, true)
  fail = false
  hub.subscriptions[0].observer.next({ Action: 'Updated', Payload: [] })
  await tick()
  assert.equal(sut.loadFailed, false)
  assert.equal(sut.loaded, true)
  assert.deepEqual(ids(sut, config), ['recovered'])
  assert.equal(requests, 3)
  assert.equal(hub.subscriptions.length, 1)
})
