import { test, expect } from '@playwright/test'
import { AUTHORITY, RESOURCEAPI_URL, getClientCredentialsToken } from './helpers'

/**
 * Direct contract tests against the ResourceApi — bypasses the BFF.
 * Verifies the JwtBearer pipeline + scope policies behave as the
 * test-apps README claims they do, so a green BFF spec doesn't mask
 * a broken resource server.
 */

test.describe('ResourceApi (direct)', () => {
  test('GET /health is anonymous', async ({ request }) => {
    const res = await request.get(`${RESOURCEAPI_URL}/health`)
    expect(res.ok()).toBeTruthy()
  })

  test('GET /me without token → 401', async ({ request }) => {
    const res = await request.get(`${RESOURCEAPI_URL}/me`)
    expect(res.status()).toBe(401)
  })

  test('GET /me with bogus token → 401', async ({ request }) => {
    const res = await request.get(`${RESOURCEAPI_URL}/me`, {
      headers: { Authorization: 'Bearer not-a-real-token' },
    })
    expect(res.status()).toBe(401)
  })

  test('GET /scoped with client_credentials demo.read → 200', async ({ request }) => {
    const token = await getClientCredentialsToken(request, 'demo.read')
    const res = await request.get(`${RESOURCEAPI_URL}/scoped`, {
      headers: { Authorization: `Bearer ${token}` },
    })
    expect(res.status()).toBe(200)
  })

  test('GET /admin with demo.read scope only → 403', async ({ request }) => {
    // demo-backend has demo.read + demo.write. /admin requires demo.admin.
    const token = await getClientCredentialsToken(request, 'demo.read')
    const res = await request.get(`${RESOURCEAPI_URL}/admin`, {
      headers: { Authorization: `Bearer ${token}` },
    })
    expect(res.status()).toBe(403)
  })

  test('Discovery doc on the IdP exposes the expected endpoints', async ({ request }) => {
    const res = await request.get(`${AUTHORITY}/.well-known/openid-configuration`)
    expect(res.ok()).toBeTruthy()
    const doc = await res.json()
    expect(doc.token_endpoint).toMatch(/\/connect\/token$/)
    expect(doc.authorization_endpoint).toMatch(/\/connect\/authorize$/)
    expect(doc.userinfo_endpoint).toMatch(/\/connect\/userinfo$/)
    expect(doc.issuer).toBe(AUTHORITY)
  })
})
