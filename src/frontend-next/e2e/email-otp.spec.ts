import { test, expect } from '@playwright/test'
import { apiLoginAndNavigate } from './helpers'

test.describe('Email OTP', () => {
  // Clean MFA state before and after each test via dev endpoint (no auth needed)
  test.beforeEach(async ({ page }) => {
    await page.request.post('/api/dev/reset-mfa/ka')
    await page.request.delete('/api/dev/emails')
  })

  test.afterEach(async ({ page }) => {
    await page.request.post('/api/dev/reset-mfa/ka')
    await page.request.delete('/api/dev/emails')
  })

  test('Enable Email OTP, login with code, then disable', async ({ page }) => {
    // Step 1: Login and enable Email OTP in profile (Security tab)
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/profile')
    await page.getByText(/sicherheit|security/i).click()
    await expect(page.getByRole('heading', { name: 'E-Mail-Code (OTP)' })).toBeVisible({ timeout: 10_000 })

    await page.getByRole('button', { name: 'E-Mail-Code aktivieren' }).click()
    // Wait for the disable button to appear (confirms enable succeeded)
    await expect(page.getByRole('button', { name: 'E-Mail-Code deaktivieren' })).toBeVisible({ timeout: 5_000 })

    // Step 2: Logout
    await page.evaluate(() => fetch('/api/account/logout', { method: 'POST' }))
    await page.goto('/login')

    // Step 3: Login with password → should require MFA
    await page.getByPlaceholder('Benutzername').fill('ka')
    await page.getByPlaceholder('Passwort').fill('Test1234!')
    await page.getByRole('button', { name: 'Anmelden', exact: true }).click()

    // Should show Email OTP code input (auto-sent since only method)
    await expect(page.getByText('Code wurde an Ihre E-Mail')).toBeVisible({ timeout: 10_000 })

    // Step 4: Get OTP code from dev endpoint
    const code = await extractOtpCode(page, 'ka@test.com')
    expect(code).toBeTruthy()

    // Step 5: Enter code and complete login
    await page.getByPlaceholder('000000').fill(code!)
    await page.getByRole('button', { name: 'Bestätigen' }).click()

    // Default post-login landing is /dashboard; just verify we left the login flow.
    await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 10_000 })

    // Step 6: Disable Email OTP (cleanup)
    await page.goto('/profile')
    await page.getByText(/sicherheit|security/i).click()
    await expect(page.getByRole('heading', { name: 'E-Mail-Code (OTP)' })).toBeVisible({ timeout: 10_000 })
    await page.getByRole('button', { name: 'E-Mail-Code deaktivieren' }).click()
    await expect(page.getByRole('button', { name: 'E-Mail-Code aktivieren' })).toBeVisible({ timeout: 5_000 })
  })

  test('Login with invalid Email OTP code shows error', async ({ page }) => {
    // Enable Email OTP via API
    await page.request.post('/api/account/login', { data: { UserName: 'ka', Password: 'Test1234!' } })
    await page.request.post('/api/account/email-otp/enable')
    await page.request.post('/api/account/logout')

    // Login with password
    await page.goto('/login')
    await page.getByPlaceholder('Benutzername').fill('ka')
    await page.getByPlaceholder('Passwort').fill('Test1234!')
    await page.getByRole('button', { name: 'Anmelden', exact: true }).click()

    await expect(page.getByPlaceholder('000000')).toBeVisible({ timeout: 10_000 })

    // Enter wrong code
    await page.getByPlaceholder('000000').fill('999999')
    await page.getByRole('button', { name: 'Bestätigen' }).click()

    // Should show error
    await expect(page.getByText('invalid', { exact: false })).toBeVisible({ timeout: 5_000 })
  })
})

/**
 * Extract 6-digit OTP code from the dev email endpoint.
 */
async function extractOtpCode(page: import('@playwright/test').Page, email: string): Promise<string | null> {
  const response = await page.request.get(`/api/dev/emails/${encodeURIComponent(email)}`)
  if (!response.ok()) return null
  const data = await response.json()
  const match = data.HtmlBody?.match(/(\d{6})/)
  return match ? match[1] : null
}
