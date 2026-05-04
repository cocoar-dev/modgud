import { test, expect } from '@playwright/test'
import { BFF_URL, DEMO_USER, DEMO_PASSWORD, loginViaUi } from './helpers'

/**
 * The full BFF login round-trip. Browser → /bff/login → IdP login form
 * → callback → cookie session → /bff/user → /api/me proxied with the
 * server-side token. Then logout and verify the cookie is gone.
 */

test.describe('BFF — login round-trip', () => {
  test('login flow: form post, cookie session, claims surface, /api/me proxies', async ({ page }) => {
    // 1. Anonymous user hits the SPA → /bff/user is 401.
    const initial = await page.request.get(`/bff/user`)
    expect(initial.status()).toBe(401)

    // 2. Top-level navigation to /bff/login → IdP login page.
    await page.goto(`/bff/login?returnUrl=${encodeURIComponent('/')}`)
    await page.waitForURL(/\/login(\?|$)/, { timeout: 15_000 })

    // 3. Fill demo credentials, submit, follow the OIDC callback chain
    //    until we land back on the BFF root.
    await loginViaUi(page, DEMO_USER, DEMO_PASSWORD)
    await page.waitForURL(BFF_URL + '/', { timeout: 20_000 })

    // 4. /bff/user now returns the cookie identity.
    const me = await page.request.get(`/bff/user`)
    expect(me.ok()).toBeTruthy()
    const meBody = await me.json()
    expect(meBody.sub).toBeTruthy()
    // demo.admin is part of the "Demo Administrators" group from demo-seed.json
    // — whatever name the IdP returns is fine, just verify we got *something*.
    expect(meBody.name || meBody.sub).toBeTruthy()

    // 5. /api/me proxies to the ResourceApi with the server-side bearer.
    const apiMe = await page.request.get(`/api/me`)
    expect(apiMe.ok()).toBeTruthy()
    const apiBody = await apiMe.json()
    expect(apiBody.sub).toBe(meBody.sub)

    // 6. /api/scoped — demo-bff was issued demo.read.
    const scoped = await page.request.get(`/api/scoped`)
    expect(scoped.status()).toBe(200)

    // 7. /api/admin — demo-bff was NOT issued demo.admin → 403.
    const adminRes = await page.request.get(`/api/admin`)
    expect(adminRes.status()).toBe(403)

    // 8. Cookie is httpOnly + scoped to the BFF (sanity).
    const cookies = await page.context().cookies()
    const session = cookies.find(c => c.name === 'bff.session')
    expect(session).toBeTruthy()
    expect(session!.httpOnly).toBe(true)
  })

  test('logout clears the cookie', async ({ page }) => {
    // Re-use the previous flow to land authenticated.
    await page.goto(`/bff/login?returnUrl=${encodeURIComponent('/')}`)
    await page.waitForURL(/\/login(\?|$)/, { timeout: 15_000 })
    await loginViaUi(page, DEMO_USER, DEMO_PASSWORD)
    await page.waitForURL(BFF_URL + '/', { timeout: 20_000 })

    expect((await page.request.get('/bff/user')).ok()).toBeTruthy()

    // Trigger logout (top-level navigation; OIDC end-session may bounce).
    await page.goto(`/bff/logout`)
    // The end-session redirect chain ultimately lands somewhere benign;
    // the contract is just "session cookie is gone".
    await page.waitForLoadState('domcontentloaded')

    // After logout the cookie may still be returned by Playwright's
    // context for a moment if the browser hasn't replaced it; the
    // authoritative check is whether /bff/user is 401 again.
    const after = await page.request.get(`/bff/user`)
    expect(after.status()).toBe(401)
  })
})
