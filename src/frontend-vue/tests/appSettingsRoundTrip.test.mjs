import assert from 'node:assert/strict'
import { test } from 'node:test'
import { keepUnchanged } from '../src/views/admin/apps/appSettingsRoundTrip.ts'

// F7-01 / F7-02 — an unchanged save must not materialize form defaults into the override.

test('an untouched inherited flag stays inherited (null), not the form default false', () => {
  const loaded = { Posture: 'Open', RequireAdminApproval: null }
  assert.equal(keepUnchanged(loaded, 'RequireAdminApproval', false, false, false), null)
})

test('an untouched "all providers" (null) list is not turned into "none" ([])', () => {
  const loaded = { InternalLoginEnabled: true, LoginProviderIds: null }
  assert.equal(keepUnchanged(loaded, 'LoginProviderIds', [], [], []), null)
})

test('a changed field sends what the admin chose, an emptied list stays explicitly empty', () => {
  const loaded = { RequireAdminApproval: null, DefaultGroupIds: ['g1'] }
  assert.equal(keepUnchanged(loaded, 'RequireAdminApproval', false, true, true), true)
  assert.deepEqual(keepUnchanged(loaded, 'DefaultGroupIds', ['g1'], [], []), [])
})

test('a section the App did not have yet behaves as before', () => {
  assert.equal(keepUnchanged(null, 'RequireAdminApproval', false, false, false), false)
})
