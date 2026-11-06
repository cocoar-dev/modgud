import assert from 'node:assert/strict'
import { test } from 'node:test'
import { consentClientWarning } from '../src/views/auth/consentClientWarning.ts'

// B10-PA03 — the host decides the unverified-client warning, for the fixed and the custom page.
test('a URL-identified client is named by its domain', () => {
  assert.deepEqual(consentClientWarning({ ClientIdHostname: 'app.example', IsDynamicallyRegistered: true }),
    { kind: 'cimd', host: 'app.example' })
})

test('a self-registered client is flagged', () => {
  assert.deepEqual(consentClientWarning({ ClientIdHostname: null, IsDynamicallyRegistered: true }), { kind: 'dcr' })
})

test('an admin-registered client gets no warning', () => {
  assert.equal(consentClientWarning({ ClientIdHostname: null, IsDynamicallyRegistered: false }), null)
  assert.equal(consentClientWarning(null), null)
})
