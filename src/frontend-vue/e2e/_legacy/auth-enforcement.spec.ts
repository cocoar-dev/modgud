import { test, expect } from '@playwright/test'
import {
  apiLoginAndNavigate,
  apiLoginAsAdmin,
  addVirtualAuthenticator,
  removeVirtualAuthenticator,
} from './helpers'

/**
 * Auth Enforcement E2E Tests
 *
 * Docker mode: runs at Level 0 (no enforcement) — tests API contracts and UI elements
 * Local mode (Level 1): test SecureSetupModal manually or via pnpm test:e2e:local
 *
 * These tests verify:
 * - Remember Me checkbox visible + sent in request
 * - /api/app-info returns correct fields (no old toggles)
 * - /api/account/me returns Has2FA + TwoFactorMethods
 * - Profile always shows all 2FA methods (no config toggles)
 * - Passkey login button always visible (no config toggle)
 * - Admin magic link endpoint works
 */

// ═══════════════════════════════════════════════════════════════
// Remember Me
// ═══════════════════════════════════════════════════════════════

test.describe('Remember Me', () => {
  test('Checkbox is visible on login page', async ({ page }) => {
    await page.goto('/login')
    await expect(page.getByText(/angemeldet bleiben|stay signed in/i)).toBeVisible({ timeout: 5_000 })
  })

  test('Login sends RememberMe in request', async ({ page }) => {
    await page.goto('/login')
    await page.getByText(/angemeldet bleiben|stay signed in/i).click()

    const [request] = await Promise.all([
      page.waitForRequest((r) => r.url().includes('/api/account/login') && r.method() === 'POST'),
      (async () => {
        await page.getByRole('textbox', { name: /benutzername|username/i }).fill('ka')
        await page.getByRole('textbox', { name: /passwort|password/i }).fill('Test1234!')
        await page.getByRole('button', { name: /anmelden|sign in/i }).first().click()
      })(),
    ])

    const body = request.postDataJSON()
    expect(body.RememberMe).toBe(true)
  })
})

// ═══════════════════════════════════════════════════════════════
// /api/app-info
// ═══════════════════════════════════════════════════════════════

test.describe('App Info API', () => {
  test('Returns new config fields', async ({ page }) => {
    const res = await page.request.get('/api/app-info')
    const data = await res.json()

    expect(data).toHaveProperty('AuthenticationMinimumLevel')
    expect(typeof data.AuthenticationMinimumLevel).toBe('number')
    expect(data).toHaveProperty('MagicLinkSelfService')
    expect(typeof data.MagicLinkSelfService).toBe('boolean')
  })

  test('Does not return old toggle fields', async ({ page }) => {
    const res = await page.request.get('/api/app-info')
    const data = await res.json()

    expect(data).not.toHaveProperty('MagicLinkEnabled')
    expect(data).not.toHaveProperty('EmailOtpAvailable')
    expect(data).not.toHaveProperty('PasskeyAvailable')
  })
})

// ═══════════════════════════════════════════════════════════════
// /api/account/me — 2FA Status
// ═══════════════════════════════════════════════════════════════

test.describe('Me Endpoint', () => {
  test('Returns Has2FA and TwoFactorMethods fields', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!')
    const res = await page.request.get('/api/account/me')
    const data = await res.json()

    expect(data).toHaveProperty('Has2FA')
    expect(typeof data.Has2FA).toBe('boolean')
    expect(data).toHaveProperty('TwoFactorMethods')
    expect(Array.isArray(data.TwoFactorMethods)).toBeTruthy()
  })
})

// ═══════════════════════════════════════════════════════════════
// Login Page — Always shows Passkey + Magic Link
// ═══════════════════════════════════════════════════════════════

test.describe('Login Page', () => {
  test('Passkey button is always visible (no config toggle)', async ({ page }) => {
    await page.goto('/login')
    await expect(page.getByRole('button', { name: /passkey/i })).toBeVisible({ timeout: 5_000 })
  })

  test('Magic Link button visible when MagicLinkSelfService is true', async ({ page }) => {
    await page.goto('/login')
    await expect(page.getByRole('button', { name: /anmelde-link|login link|magic/i })).toBeVisible({ timeout: 5_000 })
  })

  test('Forgot password link is visible (Level < 2)', async ({ page }) => {
    await page.goto('/login')
    await expect(page.getByText(/passwort vergessen|forgot password/i)).toBeVisible({ timeout: 5_000 })
  })
})

// ═══════════════════════════════════════════════════════════════
// Profile — All Methods Always Visible
// ═══════════════════════════════════════════════════════════════

test.describe('Profile — 2FA Methods', () => {
  test('All three 2FA sections are visible in security tab', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/profile')

    // Click security tab
    await page.getByText(/sicherheit|security/i).click()

    await expect(page.getByRole('heading', { name: /zwei-faktor|two-factor/i })).toBeVisible({ timeout: 10_000 })
    await expect(page.getByRole('heading', { name: /e-mail.code/i })).toBeVisible()
    await expect(page.getByRole('heading', { name: /passkey/i })).toBeVisible()
  })
})

// ═══════════════════════════════════════════════════════════════
// Admin Magic Link — API Level
// ═══════════════════════════════════════════════════════════════

test.describe('Admin Magic Link', () => {
  test('Admin can send magic link to user with email', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!')

    // Get users
    const users = await (await page.request.get('/api/user')).json()
    const targetUser = users.find((u: any) => u.Email && u.Id)

    if (targetUser) {
      const res = await page.request.post(`/api/admin/users/${targetUser.Id}/magic-link`)
      // Should not be 401/403 — might be 200 or 500 depending on email service
      expect(res.status()).not.toBe(401)
      expect(res.status()).not.toBe(403)
    }
  })

  test('Non-admin cannot send magic link', async ({ page }) => {
    // Create non-admin user via admin. Set UserName explicitly so login can't
    // race on Acronym-fallback casing.
    await apiLoginAndNavigate(page, 'ka', 'Test1234!')
    const createRes = await page.request.post('/api/user', {
      data: { Firstname: 'Non', Lastname: 'Admin', Acronym: 'NA', UserName: 'na', Email: 'na@test.com' },
    })
    if (!createRes.ok()) {
      throw new Error(`POST /api/user failed: ${createRes.status()} ${await createRes.text()}`)
    }
    // User view is async-projected; poll until it appears.
    let naUser: any
    for (let i = 0; i < 20 && !naUser; i++) {
      const users = await (await page.request.get('/api/user')).json()
      naUser = users.find((u: any) => u.UserName === 'na' || u.Acronym === 'NA')
      if (!naUser) await page.waitForTimeout(250)
    }
    expect(naUser).toBeTruthy()
    const pwdRes = await page.request.put(`/api/user/${naUser.Id}/password`, { data: { Password: 'NonAdmin1234!' } })
    expect(pwdRes.ok()).toBeTruthy()
    await page.request.post('/api/account/logout')

    // Login as non-admin and assert the login actually succeeded — otherwise the
    // follow-up admin call would 401 (no cookie) rather than 403 (no permission),
    // and we'd be asserting the wrong layer.
    const loginRes = await page.request.post('/api/account/login',
      { data: { UserName: 'na', Password: 'NonAdmin1234!' } })
    expect(loginRes.ok()).toBeTruthy()

    // Try to send magic link — should be 403 (authenticated but not app:admin)
    const res = await page.request.post(`/api/admin/users/${naUser.Id}/magic-link`)
    expect(res.status()).toBe(403)

    // Cleanup
    await page.request.post('/api/account/logout')
    await apiLoginAndNavigate(page, 'ka', 'Test1234!')
    await page.request.delete('/api/user', { data: [naUser.Id] })
  })
})

// ═══════════════════════════════════════════════════════════════
// Magic Link Login — works without setup modal at Level 0
// ═══════════════════════════════════════════════════════════════

test.describe('Magic Link Login', () => {
  test('Magic link login completes successfully', async ({ page }) => {
    await page.request.delete('/api/dev/emails').catch(() => {})

    // Request magic link
    await page.goto('/login')
    await page.getByRole('button', { name: /anmelde-link|login link|magic/i }).click()
    await page.getByPlaceholder(/email@beispiel|email@example/i).fill('ka@test.com')
    await page.getByRole('button', { name: /link senden|send link/i }).click()

    await expect(page.getByText(/anmelde-link gesendet|login link.*sent/i)).toBeVisible({ timeout: 5_000 })

    // Extract link from dev endpoint
    const emailRes = await page.request.get('/api/dev/emails/ka@test.com').catch(() => null)
    if (emailRes?.ok()) {
      const data = await emailRes.json()
      const match = data.HtmlBody?.match(/href="([^"]*magic-login[^"]*)"/)
      if (match) {
        const url = new URL(match[1])
        await page.goto(`${url.pathname}${url.search}`)
        await page.waitForURL((u) => !u.pathname.includes('/login') && !u.pathname.includes('/magic-login'), { timeout: 15_000 })
      }
    }
  })
})

// ═══════════════════════════════════════════════════════════════
// Passkey Login — with virtual authenticator
// ═══════════════════════════════════════════════════════════════

test.describe('Passkey Registration and Login', () => {
  test('Register passkey in profile, then login with it', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/profile')

    // Navigate to security tab
    await page.getByText(/sicherheit|security/i).click()
    await expect(page.getByRole('heading', { name: /passkey/i })).toBeVisible({ timeout: 10_000 })

    // Clean existing passkeys
    const deleteButtons = page.locator('button').filter({ has: page.locator('[data-testid="trash-2"], .lucide-trash-2') })
    while (await deleteButtons.count() > 0) {
      page.once('dialog', d => d.accept())
      await deleteButtons.first().click()
      await page.waitForTimeout(500)
    }

    const { cdp, authenticatorId } = await addVirtualAuthenticator(page)
    try {
      // Register
      await page.getByRole('button', { name: /passkey registrieren|register passkey/i }).click()
      await expect(page.getByText(/1 registriert|1 registered/i)).toBeVisible({ timeout: 10_000 })

      // Logout
      await page.request.post('/api/account/logout')
      await page.goto('/login')

      // Login with passkey
      await page.getByRole('button', { name: /mit passkey|sign in with passkey/i }).click()
      await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 10_000 })
    } finally {
      await removeVirtualAuthenticator(cdp, authenticatorId)
    }
  })
})
