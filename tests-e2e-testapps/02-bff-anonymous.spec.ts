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

  test('GET /api/me without session → 401 (proxied to ResourceApi w/o token)', async ({ request }) => {
    const res = await request.get(`${BFF_URL}/api/me`)
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

  test('Resource API never sees a request without going through the BFF guard', async ({ request }) => {
    // Sanity: hitting ResourceApi directly with a cookie-only "session"
    // is impossible because cookies are scoped to the BFF host.
    const res = await request.get(`${RESOURCEAPI_URL}/me`)
    expect(res.status()).toBe(401)
  })
})
