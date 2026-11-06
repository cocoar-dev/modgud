import { test, expect } from '@playwright/test'
import { BFF_URL, RESOURCEAPI_URL } from './helpers'

/**
 * BFF behaviour without a session. Covers the CSRF guard and verifies
 * that anonymous requests to the proxy are denied at the BFF, never
 * reaching the resource server with a bearer token by accident.
 */

test.describe('BFF — anonymous', () => {
  test('GET /bff/user → 401', async ({ request }) => {
    const res = await request.get(`${BFF_URL}/bff/user`)
    expect(res.status()).toBe(401)
  })

  test('GET /api/me without session → 401 at the BFF', async ({ request }) => {
    const res = await request.get(`${BFF_URL}/api/me`)
    expect(res.status()).toBe(401)
  })

  // B10-02 — a bearer the caller brings along is not a BFF session.
  test('GET /api/me with a caller-supplied bearer but no session → 401', async ({ request }) => {
    const res = await request.get(`${BFF_URL}/api/me`, {
      headers: { Authorization: 'Bearer not-a-bff-session' },
    })
    expect(res.status()).toBe(401)
  })

  test('CSRF: GET /bff/user without X-Requested-With → 400', async ({ request }) => {
    const res = await request.get(`${BFF_URL}/bff/user`, {
      headers: { 'X-Requested-With': '' },
    })
    expect(res.status()).toBe(400)
  })

  test('CSRF: GET /api/me without X-Requested-With → 400', async ({ request }) => {
    const res = await request.get(`${BFF_URL}/api/me`, {
      headers: { 'X-Requested-With': '' },
    })
    expect(res.status()).toBe(400)
  })

  test('GET /bff/login redirects to the IdP authorize endpoint', async ({ request }) => {
    const res = await request.get(`${BFF_URL}/bff/login`, {
      maxRedirects: 0,
    })
    expect([302, 303]).toContain(res.status())
    const location = res.headers()['location']!
    expect(location).toMatch(/\/connect\/authorize/)
    expect(location).toMatch(/client_id=demo-bff/)
    expect(location).toMatch(/code_challenge=/)
    expect(location).toMatch(/scope=/)
  })

  // B10-03 — the post-login target must stay on the BFF origin.
  for (const returnUrl of ['https://evil.example/', '//evil.example', '/\\evil.example']) {
    test(`GET /bff/login refuses returnUrl ${returnUrl}`, async ({ request }) => {
      const res = await request.get(`${BFF_URL}/bff/login?returnUrl=${encodeURIComponent(returnUrl)}`, {
        maxRedirects: 0,
      })
      expect(res.status()).toBe(400)
    })
  }

  test('Resource API never sees a request without going through the BFF guard', async ({ request }) => {
    // Sanity: hitting ResourceApi directly with a cookie-only "session"
    // is impossible because cookies are scoped to the BFF host.
    const res = await request.get(`${RESOURCEAPI_URL}/me`)
    expect(res.status()).toBe(401)
  })
})
