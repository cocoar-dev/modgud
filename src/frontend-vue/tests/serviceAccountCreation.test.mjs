import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createServiceAccount } from '../src/stores/serviceAccountCreation.ts'

const issued = () => ({
  Id: 'service-1', AccountName: 'worker', Purpose: 'Test account', IsActive: false, Status: 'Active',
  InitialCredential: {
    ClientSecret: 'one-time-secret',
    Credential: { Id: 'credential-1', ClientId: 'worker.initial' },
  },
})

function fixture() {
  const entities = new Map()
  const writes = []
  let complete
  let reject
  const response = new Promise((resolve, fail) => { complete = resolve; reject = fail })
  const requests = []
  const service = {
    httpClient: { post: (dto) => { requests.push(dto); return response } },
    getFromStore: (id) => entities.get(id),
    setStoreEntities: (items) => {
      writes.push(items)
      for (const item of items) entities.set(item.Id, item)
    },
  }
  return { service, entities, writes, requests, complete, reject }
}

test('HTTP first: creator gets the complete issued credential, shared store gets a separate secret-free account', async () => {
  const f = fixture()
  const input = { AccountName: 'worker', InitialCredential: { Scopes: [], AppIds: [] } }
  const pending = createServiceAccount(f.service, input)
  const response = issued()
  f.complete(response)
  const created = await pending

  assert.strictEqual(created, response)
  assert.equal(created.InitialCredential.ClientSecret, 'one-time-secret')
  assert.equal(created.InitialCredential.Credential.ClientId, 'worker.initial')
  assert.strictEqual(f.requests[0], input)
  const stored = f.entities.get(created.Id)
  assert.notStrictEqual(stored, created)
  assert.equal(stored.InitialCredential, undefined)
  assert.equal(Object.hasOwn(stored, 'InitialCredential'), false)
  assert.deepEqual(stored, {
    Id: 'service-1', AccountName: 'worker', Purpose: 'Test account', IsActive: false, Status: 'Active',
  })
  assert.equal(f.writes.length, 1)
})

test('feed first: preserve the newer feed entry but return the HTTP-only credential to the creator', async () => {
  const f = fixture()
  const pending = createServiceAccount(f.service, { AccountName: 'worker' })
  const feedEntry = { Id: 'service-1', AccountName: 'worker-renamed', IsActive: true, Status: 'Active' }
  f.entities.set(feedEntry.Id, feedEntry)
  const response = issued()
  f.complete(response)

  const created = await pending
  assert.strictEqual(created, response)
  assert.equal(created.InitialCredential.ClientSecret, 'one-time-secret')
  assert.equal(created.InitialCredential.Credential.ClientId, 'worker.initial')
  assert.strictEqual(f.entities.get(feedEntry.Id), feedEntry)
  assert.equal(feedEntry.InitialCredential, undefined)
  assert.equal(f.writes.length, 0)
})

test('a later clean feed update does not remove the credential from the already returned response', async () => {
  const f = fixture()
  const pending = createServiceAccount(f.service, { AccountName: 'worker' })
  f.complete(issued())
  const created = await pending
  const feedEntry = { ...f.entities.get(created.Id), Purpose: 'Updated' }
  f.entities.set(feedEntry.Id, feedEntry)

  assert.equal(created.InitialCredential.ClientSecret, 'one-time-secret')
  assert.equal(f.entities.get(created.Id).InitialCredential, undefined)
  assert.notStrictEqual(f.entities.get(created.Id), created)
})

test('creation without an initial credential retains server-normalized fields', async () => {
  const f = fixture()
  const pending = createServiceAccount(f.service, { AccountName: ' WORKER ', IsActive: false })
  const response = { Id: 'service-2', AccountName: 'worker', Purpose: null, IsActive: false, Status: 'Active' }
  f.complete(response)

  assert.strictEqual(await pending, response)
  assert.deepEqual(f.entities.get(response.Id), response)
  assert.equal(f.entities.get(response.Id).InitialCredential, undefined)
})

test('a failed create rejects without adding an input-shaped or secret-bearing store entry', async () => {
  const f = fixture()
  const failure = new Error('Create refused')
  const pending = createServiceAccount(f.service, { AccountName: 'worker' })
  f.reject(failure)

  await assert.rejects(pending, (error) => error === failure)
  assert.equal(f.entities.size, 0)
  assert.equal(f.writes.length, 0)
})
