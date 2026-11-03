import assert from 'node:assert/strict'
import { afterEach, beforeEach, test } from 'node:test'
import { HttpClientError, useHttpClient } from '../src/composables/useHttpClient.ts'

// No backend, browser, Vue runtime or network: exercise the actual HTTP client
// against synthetic responses. Tests are sequential because they mock browser globals.
let previousWindow
let previousStorage
let storage

beforeEach(() => {
  previousWindow = Object.getOwnPropertyDescriptor(globalThis, 'window')
  previousStorage = Object.getOwnPropertyDescriptor(globalThis, 'sessionStorage')
  storage = new Map()
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { location: { pathname: '/profile', search: '', href: '/profile' } },
  })
  Object.defineProperty(globalThis, 'sessionStorage', {
    configurable: true,
    value: { getItem: (key) => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
  })
})

afterEach(() => {
  for (const [name, descriptor] of [['window', previousWindow], ['sessionStorage', previousStorage]]) {
    if (descriptor) Object.defineProperty(globalThis, name, descriptor)
    else delete globalThis[name]
  }
})

function respond(t, status, body) {
  return t.mock.method(globalThis, 'fetch', async () => new Response(JSON.stringify(body), {
    status,
    statusText: status === 401 ? 'Unauthorized' : 'Forbidden',
    headers: { 'Content-Type': 'application/json' },
  }))
}

function isHttpError(status, body) {
  return (error) => {
    assert.ok(error instanceof HttpClientError)
    assert.equal(error.status, status)
    assert.deepEqual(error.body, body)
    return true
  }
}

for (const flag of ['RequiresSecureSetup', 'RequiresStepUp']) {
  for (const path of ['/api/account/profile/request', '/api/account/external-links', '/api/account/passkey', '/api/inbox']) {
    test(`${path}: background ${flag} rejects locally without navigation or consuming the guard`, async (t) => {
      const body = { [flag]: true }
      const fetch = respond(t, 403, body)
      await assert.rejects(useHttpClient(path).get({ redirectOnStepUp: false }), isHttpError(403, body))
      assert.equal(window.location.href, '/profile')
      assert.equal(storage.size, 0)
      assert.equal(fetch.mock.calls[0].arguments[0], path)
    })
  }

  test(`default GET still navigates for ${flag}`, async (t) => {
    respond(t, 403, { [flag]: true })
    await assert.rejects(useHttpClient('/api/account/profile/request').get(), HttpClientError)
    assert.equal(window.location.href, '/login?stepup=1&redirect=%2Fprofile')
    assert.equal(storage.size, 1)
  })

  test(`a background GET does not opt a later mutation out of ${flag}`, async (t) => {
    const body = { [flag]: true }
    respond(t, 403, body)
    const client = useHttpClient('/api/account/profile').addPath('request')
    await assert.rejects(client.get({ redirectOnStepUp: false }), isHttpError(403, body))
    assert.equal(window.location.href, '/profile')
    await assert.rejects(client.put({ Firstname: 'Changed' }), isHttpError(403, body))
    assert.equal(window.location.href, '/login?stepup=1&redirect=%2Fprofile')
  })
}

test('a locally handled background refusal leaves the allowed deletion request usable', async (t) => {
  const calls = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    calls.push({ url, method: init.method })
    if (url === '/api/account/profile/request') {
      return new Response(JSON.stringify({ RequiresSecureSetup: true }), { status: 403 })
    }
    return new Response(null, { status: 204 })
  })
  await assert.rejects(useHttpClient('/api/account/profile/request').get({ redirectOnStepUp: false }), HttpClientError)
  await useHttpClient('/api/auth').addPath('delete-account').post({ Reason: null })
  assert.equal(window.location.href, '/profile')
  assert.deepEqual(calls, [
    { url: '/api/account/profile/request', method: 'GET' },
    { url: '/api/auth/delete-account', method: 'POST' },
  ])
})

test('deletion with a stale account proof still redirects to reauthentication', async (t) => {
  const body = { RequiresReauthentication: true }
  respond(t, 403, body)
  await assert.rejects(useHttpClient('/api/auth/delete-account').post({ Reason: null }), isHttpError(403, body))
  assert.equal(window.location.href, '/login?stepup=1&reauth=1&redirect=%2Fprofile')
})

test('GET opt-out does not suppress a fresh account-proof requirement', async (t) => {
  const body = { RequiresReauthentication: true }
  respond(t, 403, body)
  await assert.rejects(useHttpClient('/api/account/profile/request').get({ redirectOnStepUp: false }), isHttpError(403, body))
  assert.equal(window.location.href, '/login?stepup=1&reauth=1&redirect=%2Fprofile')
})

test('administration requests retain their own step-up target', async (t) => {
  respond(t, 403, { RequiresStepUp: true })
  await assert.rejects(useHttpClient('/api/admin/users').get(), HttpClientError)
  assert.equal(window.location.href, '/login?stepup=1&for=admin&redirect=%2Fprofile')
})

test('the existing repeated-navigation guard still applies to default requests', async (t) => {
  respond(t, 403, { RequiresStepUp: true })
  const client = useHttpClient('/api/account/profile/request')
  await assert.rejects(client.get(), HttpClientError)
  window.location.href = '/profile'
  await assert.rejects(client.get(), HttpClientError)
  assert.equal(window.location.href, '/profile')
})

test('default step-up navigation is allowed again after the ten-second guard expires', async (t) => {
  let now = 1_000
  t.mock.method(Date, 'now', () => now)
  respond(t, 403, { RequiresStepUp: true })
  const client = useHttpClient('/api/account/profile/request')
  await assert.rejects(client.get(), HttpClientError)
  window.location.href = '/profile'
  now += 10_001
  await assert.rejects(client.get(), HttpClientError)
  assert.equal(window.location.href, '/login?stepup=1&redirect=%2Fprofile')
})

for (const method of ['post', 'put', 'patch', 'delete']) {
  test(`${method} still requires step-up after a background GET on the same client`, async (t) => {
    respond(t, 403, { RequiresStepUp: true })
    const client = useHttpClient('/api/account/profile/request')
    await assert.rejects(client.get({ redirectOnStepUp: false }), HttpClientError)
    assert.equal(window.location.href, '/profile')
    await assert.rejects(client[method]({}), HttpClientError)
    assert.equal(window.location.href, '/login?stepup=1&redirect=%2Fprofile')
  })
}

test('ordinary permission refusals remain local errors', async (t) => {
  const body = { Message: 'Permission denied' }
  respond(t, 403, body)
  await assert.rejects(useHttpClient('/api/account/profile/request').get(), isHttpError(403, body))
  assert.equal(window.location.href, '/profile')
})

test('an expired session still redirects to login even on an optional background GET', async (t) => {
  respond(t, 401, {})
  await assert.rejects(useHttpClient('/api/account/profile/request').get({ redirectOnStepUp: false }), HttpClientError)
  assert.equal(window.location.href, '/login')
})

test('the expected unauthenticated me response still stays local', async (t) => {
  respond(t, 401, {})
  await assert.rejects(useHttpClient('/api/account/me').get(), HttpClientError)
  assert.equal(window.location.href, '/profile')
})

test('GET options do not change URL builders, successful data or default sibling clients', async (t) => {
  const base = useHttpClient('/api/account/profile')
  const background = base.addPath('request').setQueryParameter('view', 'a b')
  const fetch = t.mock.method(globalThis, 'fetch', async () => new Response(JSON.stringify({ Open: null, LastTerminal: null })))
  assert.deepEqual(await background.get({ redirectOnStepUp: false }), { Open: null, LastTerminal: null })
  assert.equal(fetch.mock.calls[0].arguments[0], '/api/account/profile/request?view=a+b')
  assert.equal(fetch.mock.calls[0].arguments[1].method, 'GET')
  respond(t, 403, { RequiresSecureSetup: true })
  await assert.rejects(base.addPath('request').get(), HttpClientError)
  assert.equal(window.location.href, '/login?stepup=1&redirect=%2Fprofile')
})

const mfaPosts = [
  '/api/account/mfa/login',
  '/api/account/email-otp/login',
  '/api/account/email-otp/login/request',
]
for (const path of mfaPosts) {
  test(`${path}: expected POST401 rejects locally, preserving all continuation flags`, async t => {
    const here = '/login?mfa=totp&stepup=1&reauth=1&for=admin&redirect=%2Fadmin%2Fusers'
    window.location.href = here
    const body = { Message: 'Invalid credentials' }
    const fetch = respond(t, 401, body)
    await assert.rejects(useHttpClient(path).post({ Code: 'synthetic' }), isHttpError(401, body))
    assert.equal(window.location.href, here)
    assert.equal(fetch.mock.callCount(), 1)
    assert.equal(storage.size, 0)
  })

  for (const method of ['get', 'put', 'patch', 'delete']) {
    test(`${path}: ${method}401 is not a MFA-POST navigation exception`, async t => {
      respond(t, 401, {})
      await assert.rejects(useHttpClient(path)[method](), HttpClientError)
      assert.equal(window.location.href, '/login')
    })
  }
}

for (const path of [
  '/api/account/mfa/setup',
  '/api/account/email-otp/enable',
  '/api/account/mfa/login/other',
  '/api/account/mfa/login?other=1',
  '/api/account/step-up',
]) {
  test(`${path}: nearby POST401 keeps the normal session-expiry redirect`, async t => {
    respond(t, 401, {})
    await assert.rejects(useHttpClient(path).post({}), HttpClientError)
    assert.equal(window.location.href, '/login')
  })
}

test('local MFA refusal does not opt protected sibling requests out of401 handling', async t => {
  respond(t, 401, {})
  const account = useHttpClient('/api/account')
  await assert.rejects(account.addPath('mfa', 'login').post({}), HttpClientError)
  assert.equal(window.location.href, '/profile')
  await assert.rejects(account.addPath('profile', 'request').get(), HttpClientError)
  assert.equal(window.location.href, '/login')
})

test('MFA POST400 still rejects locally and a later successful POST returns normally', async t => {
  const body = { detail: 'Invalid code' }
  respond(t, 400, body)
  await assert.rejects(useHttpClient('/api/account/email-otp/login').post({}), isHttpError(400, body))
  assert.equal(window.location.href, '/profile')
  t.mock.method(globalThis, 'fetch', async () => new Response(JSON.stringify({ Message: 'Login successful' })))
  assert.deepEqual(await useHttpClient('/api/account/mfa/login').post({}), { Message: 'Login successful' })
})
