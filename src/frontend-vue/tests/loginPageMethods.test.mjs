import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { loginPageMissingMethods } from '../src/page-builder/loginPageMethods.ts'

const template = JSON.parse(readFileSync(new URL('../src/page-builder/documents/login.json', import.meta.url), 'utf8'))

// B10-PA02 — the shipped login template must offer every sign-in method, e-mail code included.
test('the shipped login template offers every sign-in method', () => {
  assert.deepEqual(loginPageMissingMethods(template), [])
})

test('a page without the code actions is reported as missing e-mail code', () => {
  const page = { id: 'p', type: 'page', children: [
    { id: 'a', type: 'button', props: { action: 'auth:login' } },
    { id: 'b', type: 'button', props: { action: 'auth:passkey' } },
    { id: 'c', type: 'button', props: { action: 'auth:magic-link' } },
    { id: 'd', type: 'button', props: { action: 'auth:request-login-code' } },
  ] }
  assert.deepEqual(loginPageMissingMethods(page), ['emailCode'])
})
