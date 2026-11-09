import assert from 'node:assert/strict'
import { test } from 'node:test'
import { adoptOwnVersion } from '../src/composables/adoptOwnVersion.ts'

// R1 (post-merge review) — the view's own write to the settings document must not make its next
// save a 409, while a change by someone else must still do so.

function doc(version) {
  const state = { version }
  return { state, fetchVersion: async () => state.version }
}

test('the version after the view\'s own write is adopted', async () => {
  const d = doc('v1')
  let held = 'v1'
  const result = await adoptOwnVersion(held, d.fetchVersion, async () => { d.state.version = 'v2'; return 'ok' }, (v) => { held = v })
  assert.equal(result, 'ok')
  assert.equal(held, 'v2')
})

test('a change by someone else before the write is not adopted', async () => {
  const d = doc('v-other')
  let held = 'v1'
  await adoptOwnVersion(held, d.fetchVersion, async () => { d.state.version = 'v3' }, (v) => { held = v })
  assert.equal(held, 'v1')
})

test('without a held version the write runs alone', async () => {
  let fetched = 0
  let adopted = false
  await adoptOwnVersion(null, async () => { fetched++; return 'v1' }, async () => {}, () => { adopted = true })
  assert.equal(fetched, 0)
  assert.equal(adopted, false)
})

test('a failed write adopts nothing', async () => {
  const d = doc('v1')
  let held = 'v1'
  await assert.rejects(adoptOwnVersion(held, d.fetchVersion, async () => { throw new Error('boom') }, (v) => { held = v }))
  assert.equal(held, 'v1')
})
