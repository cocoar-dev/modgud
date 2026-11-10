import assert from 'node:assert/strict'
import { test } from 'node:test'
import { loginErrorText } from '../src/views/auth/loginErrors.ts'

// The login page showed every 403 as "password sign-in is disabled", also an unconfirmed address.

test('an unconfirmed address is named as such, not as password sign-in being off', () => {
  const text = loginErrorText(403, 'Forbidden', { Code: 'Account.EmailNotVerified', Message: 'Please verify your email address before signing in.' })
  assert.equal(text.key, 'auth.login.emailNotVerified')
})

test('password sign-in off and internal login off keep their own messages', () => {
  assert.equal(loginErrorText(403, 'Forbidden', { Code: 'Account.PasswordLoginDisabled' }).key, 'auth.login.passwordDisabled')
  assert.equal(loginErrorText(403, 'Forbidden', { Code: 'Account.InternalLoginDisabled' }).key, 'auth.login.internalLoginDisabled')
})

test('an unknown 403 shows the server message instead of guessing a cause', () => {
  const text = loginErrorText(403, 'Forbidden', { Message: 'Something specific' })
  assert.equal(text.key, 'auth.login.error')
  assert.deepEqual(text.params, { detail: 'Something specific' })
})

test('401 stays "invalid username or password"', () => {
  assert.equal(loginErrorText(401, 'Unauthorized', null).key, 'auth.login.invalidCredentials')
})
