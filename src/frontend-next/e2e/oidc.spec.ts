import { test, expect, request as pwRequest } from '@playwright/test'
import { createTestIdpConfig, deleteTestIdpConfig } from './helpers'

/**
 * OIDC E2E — drives the full browser flow through the dockerized TestIdP.
 *
 * Each test creates a fresh IdpConfig (admin API) and tears it down after.
 * Container state (PG + App + TestIdP) is shared across the whole run —
 * see global-setup.ts.
 */

const ADMIN = { userName: 'ka', password: 'Test1234!' }

async function adminApi(baseURL: string) {
  const ctx = await pwRequest.newContext({ baseURL })
  const loginRes = await ctx.post('/api/account/login', {
    data: { UserName: ADMIN.userName, Password: ADMIN.password },
  })
  if (!loginRes.ok()) throw new Error(`Admin login failed: ${loginRes.status()}`)
  return ctx
}

/**
 * Polls /api/user until a user with the given username appears, then returns
 * the total user count. UserView is an async MultiStreamProjection — a fresh
 * JIT-created user is on /api/account/me (reads ApplicationUser directly) a
 * moment before the projection catches up and populates /api/user.
 */
async function pollUntilUserAppears(ctx: Awaited<ReturnType<typeof adminApi>>, expectedUserName: string, timeoutMs = 5000): Promise<number> {
  const deadline = Date.now() + timeoutMs
  let lastList: Array<{ UserName: string }> = []
  while (Date.now() < deadline) {
    lastList = await (await ctx.get('/api/user')).json()
    if (lastList.some(u => u.UserName === expectedUserName)) return lastList.length
    await new Promise(r => setTimeout(r, 200))
  }
  throw new Error(`User '${expectedUserName}' did not appear in /api/user within ${timeoutMs}ms. Saw: ${lastList.map(u => u.UserName).join(', ')}`)
}

test.describe('OIDC federated login', () => {
  let configId: string | null = null
  let baseURL: string

  test.beforeAll(({ baseURL: b }) => {
    baseURL = b!
  })

  test.afterEach(async () => {
    if (configId) {
      const api = await adminApi(baseURL)
      await deleteTestIdpConfig(api, configId)
      await api.dispose()
      configId = null
    }
  })

  test('happy path — OIDC login JIT-creates a user and lands on dashboard', async ({ page }) => {
    // ── Arrange: admin creates the IdpConfig + registers callback with TestIdP
    const api = await adminApi(baseURL)
    try {
      const config = await createTestIdpConfig(api, { displayName: 'TestIdP Happy' })
      configId = config.id
    } finally {
      await api.dispose()
    }

    // ── Act: start OIDC from the login page as an anonymous user
    await page.goto('/login')
    await expect(page.getByRole('button', { name: /TestIdP Happy/i })).toBeVisible({ timeout: 10_000 })

    // Clicking the IdP button redirects to the app's /start → TestIdP /authorize.
    // TestIdP isn't authenticated, so it redirects to its own /login page.
    await page.getByRole('button', { name: /TestIdP Happy/i }).click()
    await page.waitForURL(/\/login\?returnUrl=/, { timeout: 15_000 })

    // The TestIdP login page has a user select + password (pre-filled).
    await page.selectOption('select[name="userName"]', 'alice')
    await page.getByRole('button', { name: /Sign in/i }).click()

    // ── Assert: after the OIDC round-trip, the browser lands on /dashboard
    await page.waitForURL(/\/dashboard/, { timeout: 30_000 })

    // /api/account/me now reports alice's IdP-sourced data
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    const meJson = await me.json()
    expect(meJson.Email).toBe('alice@e2e.test')
  })

  test('returning user — second OIDC login reuses the link, no duplicate user', async ({ page, browser }) => {
    const api = await adminApi(baseURL)
    try {
      const config = await createTestIdpConfig(api, { displayName: 'TestIdP Returning' })
      configId = config.id
    } finally {
      await api.dispose()
    }

    // ── First login
    await page.goto('/login')
    await page.getByRole('button', { name: /TestIdP Returning/i }).click()
    await page.waitForURL(/\/login\?returnUrl=/, { timeout: 15_000 })
    await page.selectOption('select[name="userName"]', 'bob')
    await page.getByRole('button', { name: /Sign in/i }).click()
    await page.waitForURL(/\/dashboard/, { timeout: 30_000 })

    // Snapshot: how many users exist now? Admin needs to check.
    // UserView is an async projection — wait until bob shows up before
    // taking the snapshot, otherwise we race the Marten daemon.
    const adminCtx = await adminApi(baseURL)
    const countBefore = await pollUntilUserAppears(adminCtx, 'bob')
    await adminCtx.dispose()

    // ── Second login from a clean browser context
    const ctx2 = await browser.newContext()
    const page2 = await ctx2.newPage()
    try {
      await page2.goto(`${baseURL}/login`)
      await page2.getByRole('button', { name: /TestIdP Returning/i }).click()
      await page2.waitForURL(/\/login\?returnUrl=/, { timeout: 15_000 })
      await page2.selectOption('select[name="userName"]', 'bob')
      await page2.getByRole('button', { name: /Sign in/i }).click()
      await page2.waitForURL(/\/dashboard/, { timeout: 30_000 })
    } finally {
      await ctx2.close()
    }

    // No new user created — the second login reused bob's existing link.
    // Wait briefly so the async UserView projection has caught up if a new
    // user WERE created; if it's not there after the wait, we're stable.
    await page.waitForTimeout(1000)
    const adminCtx2 = await adminApi(baseURL)
    const usersAfterJson = await (await adminCtx2.get('/api/user')).json() as Array<{ Id: string; UserName: string; Email: string | null }>
    const countAfter = usersAfterJson.length
    await adminCtx2.dispose()

    expect(countAfter).toBe(countBefore)
  })
})
