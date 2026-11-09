import assert from 'node:assert/strict'
import { beforeEach, afterEach, test } from 'node:test'
import { registerHooks } from 'node:module'

// Execute the real composable, HTTP client and Vue reactivity. Only the hub is
// fake; no server, backend config or network is used. Node's resolver needs the
// .ts extension normally supplied by Vite and an isolated SignalR transport.
const entityUrl = new URL('../src/composables/useEntityService.ts', import.meta.url).href
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
    if (context.parentURL === entityUrl && specifier === './useSignalR')
      return { url: hubUrl, shortCircuit: true }
    if (context.parentURL === entityUrl && specifier === './useHttpClient')
      return next(new URL('../src/composables/useHttpClient.ts', import.meta.url).href, context)
    return next(specifier, context)
  },
})
const { useEntityService } = await import(entityUrl)
const { HttpClientError } = await import('../src/composables/useHttpClient.ts')
const { hub } = await import(hubUrl)
hooks.deregister()

let originalWindow, originalStorage
beforeEach(() => {
  hub.subscriptions.length = 0
  hub.reconnects.length = 0
  originalWindow = Object.getOwnPropertyDescriptor(globalThis, 'window')
  originalStorage = Object.getOwnPropertyDescriptor(globalThis, 'sessionStorage')
  Object.defineProperty(globalThis, 'window', { configurable: true,
    value: { location: { pathname: '/profile', search: '', href: '/profile' } } })
  const storage = new Map()
  Object.defineProperty(globalThis, 'sessionStorage', { configurable: true,
    value: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) } })
})
afterEach(() => {
  for (const [key, descriptor] of [['window', originalWindow], ['sessionStorage', originalStorage]]) {
    if (descriptor) Object.defineProperty(globalThis, key, descriptor)
    else delete globalThis[key]
  }
})

const service = (config = {}) => useEntityService({
  apiPath: '/api/inbox', entityName: 'Inbox', loadOnInit: false,
  redirectOnLoadStepUp: false, ...config,
})
const response = (status, body) => new Response(JSON.stringify(body), { status,
  headers: { 'Content-Type': 'application/json' } })

for (const kind of ['permission', 'setup', 'stepup', 'network']) {
  test(`reconnect ${kind} failure is handled, invalidates stale data and a later reload recovers`, async t => {
    let requests = 0
    let fail = true
    t.mock.method(globalThis, 'fetch', async () => {
      requests++
      if (!fail) return response(200, [{ Id: 'fresh' }])
      if (kind === 'network') throw new TypeError('Deliberate transport failure')
      return response(403, kind === 'setup' ? { RequiresSecureSetup: true }
        : kind === 'stepup' ? { RequiresStepUp: true } : { Message: 'Forbidden' })
    })
    const sut = service()
    sut.setAllStoreEntities([{ Id: 'stale' }])
    await sut.initialize()
    await sut.initialize()
    assert.equal(hub.subscriptions.length, 1)
    assert.equal(hub.reconnects.length, 1)
    await hub.reconnects[0].callback()
    assert.equal(requests, 1)
    assert.deepEqual(sut.entities.value, [])
    assert.deepEqual(sut.entityMap.value, {})
    assert.equal(sut.allLoaded.value, false)
    assert.equal(sut.loadFailed.value, true)
    assert.equal(window.location.href, '/profile')
    hub.subscriptions[0].observer.next({ Action: 'Updated', Payload: [{ Id: 'partial' }] })
    assert.equal(sut.loadFailed.value, true)
    assert.equal(sut.allLoaded.value, false)
    fail = false
    await hub.reconnects[0].callback()
    assert.equal(requests, 2)
    assert.deepEqual(sut.entities.value, [{ Id: 'fresh' }])
    assert.equal(sut.loadFailed.value, false)
    assert.equal(sut.allLoaded.value, true)
    assert.equal(hub.subscriptions.length, 1)
    assert.equal(hub.reconnects.length, 1)
  })
}

test('an empty first load failure is different from a successful empty list', async t => {
  let fail = true
  t.mock.method(globalThis, 'fetch', async () => response(fail ? 503 : 200, fail ? {} : []))
  const sut = service()
  await assert.rejects(sut.loadAll(), HttpClientError)
  assert.equal(sut.loadFailed.value, true)
  assert.equal(sut.allLoaded.value, false)
  fail = false
  assert.deepEqual(await sut.loadAll(), [])
  assert.equal(sut.loadFailed.value, false)
  assert.equal(sut.allLoaded.value, true)
})

test('explicit initialize/loadAll and optimistic CRUD still reject to their callers', async t => {
  t.mock.method(globalThis, 'fetch', async () => response(403, { Message: 'Forbidden' }))
  const sut = service({ loadOnInit: true })
  await assert.rejects(sut.initialize(), HttpClientError)
  await assert.rejects(sut.loadAll(), HttpClientError)
  sut.setAllStoreEntities([{ Id: 'existing', Name: 'before' }])
  await assert.rejects(sut.updateEntity('existing', { Name: 'after' }), HttpClientError)
  assert.deepEqual(sut.getFromStore('existing'), { Id: 'existing', Name: 'before' })
  await assert.rejects(sut.createEntity({ Name: 'new' }), HttpClientError)
  await assert.rejects(sut.deleteEntities(['existing']), HttpClientError)
  assert.equal(sut.entities.value.length, 1)
})

test('FullSync owns its snapshot and never registers a REST reconnect reload', async t => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => response(200, []))
  const sut = service({ handleFullSync: true })
  await sut.initialize()
  assert.equal(hub.reconnects.length, 0)
  hub.subscriptions[0].observer.next({ Action: 'FullSync', Payload: [{ Id: 'sync' }] })
  assert.deepEqual(sut.entities.value, [{ Id: 'sync' }])
  assert.equal(sut.allLoaded.value, true)
  assert.equal(sut.loadFailed.value, false)
  assert.equal(fetch.mock.calls.length, 0)
})

test('disabled SignalR neither subscribes nor registers background reloads', async () => {
  await service({ enableSignalR: false }).initialize()
  assert.equal(hub.subscriptions.length, 0)
  assert.equal(hub.reconnects.length, 0)
})

for (const [status, body, config, expected] of [
  [401, {}, {}, '/login'],
  [403, { RequiresReauthentication: true }, {}, '/login?stepup=1&reauth=1&redirect=%2Fprofile'],
  [403, { RequiresSecureSetup: true }, { redirectOnLoadStepUp: true }, '/login?stepup=1&redirect=%2Fprofile'],
]) {
  test(`background catch does not suppress the ${status}/${Object.keys(body)[0] ?? 'expired-session'} gate`, async t => {
    t.mock.method(globalThis, 'fetch', async () => response(status, body))
    const sut = service(config)
    await sut.initialize()
    await hub.reconnects[0].callback()
    assert.equal(window.location.href, expected)
    assert.equal(sut.loadFailed.value, true)
  })
}

function deferred() {
  let resolve
  const promise = new Promise(r => { resolve = r })
  return { promise, resolve }
}

for (const latest of ['read', 'FullSync']) {
  for (const oldOutcome of ['success', 'failure']) {
    test(`an older ${oldOutcome} cannot overwrite or invalidate a newer ${latest} snapshot`, async t => {
      const delayed = deferred()
      let requests = 0
      t.mock.method(globalThis, 'fetch', async () => {
        if (++requests === 1) return delayed.promise
        return response(200, [{ Id: 'newer' }])
      })
      const sut = service({ handleFullSync: latest === 'FullSync' })
      await sut.initialize()
      const older = sut.loadAll()
      const completed = oldOutcome === 'failure' ? assert.rejects(older, HttpClientError) : older
      if (latest === 'read') await sut.loadAll()
      else hub.subscriptions[0].observer.next({ Action: 'FullSync', Payload: [{ Id: 'newer' }] })
      delayed.resolve(response(oldOutcome === 'success' ? 200 : 503, oldOutcome === 'success' ? [{ Id: 'older' }] : {}))
      await completed
      assert.deepEqual(sut.entities.value, [{ Id: 'newer' }])
      assert.equal(sut.allLoaded.value, true)
      assert.equal(sut.loadFailed.value, false)
    })
  }
}

test('an older success cannot hide the latest full-read failure', async t => {
  const delayed = deferred()
  let requests = 0
  t.mock.method(globalThis, 'fetch', async () => ++requests === 1 ? delayed.promise : response(503, {}))
  const sut = service()
  const older = sut.loadAll()
  await assert.rejects(sut.loadAll(), HttpClientError)
  delayed.resolve(response(200, [{ Id: 'older' }]))
  await older
  assert.deepEqual(sut.entities.value, [])
  assert.equal(sut.loadFailed.value, true)
  assert.equal(sut.allLoaded.value, false)
})

test('FullSync explicitly recovers a previously failed complete list', async t => {
  t.mock.method(globalThis, 'fetch', async () => response(503, {}))
  const sut = service({ handleFullSync: true })
  await sut.initialize()
  await assert.rejects(sut.loadAll(), HttpClientError)
  assert.equal(sut.loadFailed.value, true)
  hub.subscriptions[0].observer.next({ Action: 'FullSync', Payload: [{ Id: 'sync' }] })
  assert.equal(sut.loadFailed.value, false)
  assert.equal(sut.allLoaded.value, true)
  assert.deepEqual(sut.entities.value, [{ Id: 'sync' }])
})

test('a mutation rollback can partially refill an invalidated list but does not hide its failure', async t => {
  const mutation = deferred()
  t.mock.method(globalThis, 'fetch', async (_url, init) =>
    init.method === 'PUT' ? mutation.promise : response(403, { Message: 'Forbidden' }))
  const sut = service()
  sut.setAllStoreEntities([{ Id: 'existing', Name: 'before' }])
  await sut.initialize()
  const updated = sut.updateEntity('existing', { Name: 'after' })
  const rejected = assert.rejects(updated, HttpClientError)
  await hub.reconnects[0].callback()
  assert.deepEqual(sut.entities.value, [])
  mutation.resolve(response(403, { Message: 'Forbidden' }))
  await rejected
  assert.deepEqual(sut.entities.value, [{ Id: 'existing', Name: 'before' }])
  assert.equal(sut.loadFailed.value, true)
  assert.equal(sut.allLoaded.value, false)
})

// B10-UI02 — a REST snapshot that started before a live change must not undo it, and a
// failed optimistic PUT must not roll back a state the server pushed meanwhile.
test('a snapshot that started before a live change does not undo it', async t => {
  const get = deferred()
  t.mock.method(globalThis, 'fetch', async () => get.promise)
  const sut = service()
  await sut.initialize()
  const loading = sut.loadAll()
  hub.subscriptions[0].observer.next({ Action: 'Updated', Payload: [{ Id: 'a', Name: 'new' }] })
  hub.subscriptions[0].observer.next({ Action: 'Deleted', Payload: ['b'] })
  hub.subscriptions[0].observer.next({ Action: 'Created', Payload: [{ Id: 'c', Name: 'created' }] })
  get.resolve(response(200, [{ Id: 'a', Name: 'old' }, { Id: 'b', Name: 'gone' }]))
  await loading
  assert.deepEqual(sut.getFromStore('a'), { Id: 'a', Name: 'new' })
  assert.equal(sut.getFromStore('b'), undefined)
  assert.deepEqual(sut.getFromStore('c'), { Id: 'c', Name: 'created' })
})

test('a failed update does not roll back a state pushed meanwhile', async t => {
  const put = deferred()
  t.mock.method(globalThis, 'fetch', async () => put.promise)
  const sut = service()
  await sut.initialize()
  sut.setAllStoreEntities([{ Id: 'a', Name: 'v1' }])
  const updating = sut.updateEntity('a', { Name: 'mine' })
  hub.subscriptions[0].observer.next({ Action: 'Updated', Payload: [{ Id: 'a', Name: 'v2' }] })
  put.resolve(response(500, { Message: 'boom' }))
  await assert.rejects(updating, HttpClientError)
  assert.deepEqual(sut.getFromStore('a'), { Id: 'a', Name: 'v2' })
})

// B10-UI03 — an entity stream the server completed (e.g. refused for a missing read right)
// or that failed must be subscribed again by a later initialize, not stay dead.
for (const end of ['complete', 'error']) {
  test(`an entity stream that ended with ${end} is subscribed again on the next initialize`, async t => {
    t.mock.method(globalThis, 'fetch', async () => response(200, []))
    const sut = service()
    await sut.initialize()
    assert.equal(hub.subscriptions.length, 1)
    if (end === 'complete') hub.subscriptions[0].observer.complete()
    else hub.subscriptions[0].observer.error(new Error('stream failed'))
    await sut.initialize()
    assert.equal(hub.subscriptions.length, 2)
    assert.equal(hub.reconnects.length, 1)
  })
}
