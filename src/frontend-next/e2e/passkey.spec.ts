import { test, expect } from '@playwright/test'
import { apiLoginAndNavigate, addVirtualAuthenticator, removeVirtualAuthenticator } from './helpers'

test.describe('Passkey / WebAuthn', () => {
  test('Register passkey and login with it', async ({ page }) => {
    // Step 1: Login and go to profile → Security tab (passkey lives there)
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/profile')
    await page.getByText(/sicherheit|security/i).click()
    await expect(page.getByRole('heading', { name: /passkey/i })).toBeVisible({ timeout: 10_000 })

    // Clean up any existing passkeys first
    await deleteAllPasskeys(page)

    // Step 2: Add virtual authenticator
    const { cdp, authenticatorId } = await addVirtualAuthenticator(page)

    try {
      // Step 3: Register a passkey
      await page.getByRole('button', { name: 'Passkey registrieren' }).click()

      // Wait for registration to complete
      await expect(page.getByText('1 registriert')).toBeVisible({ timeout: 10_000 })

      // Step 4: Logout
      await page.request.post('/api/account/logout')
      await page.goto('/login')
      await expect(page.getByRole('button', { name: 'Anmelden', exact: true })).toBeVisible()

      // Step 5: Login with passkey
      await page.getByRole('button', { name: 'Mit Passkey anmelden' }).click()

      // Virtual authenticator auto-responds; router lands on /dashboard by default.
      await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 10_000 })
    } finally {
      await removeVirtualAuthenticator(cdp, authenticatorId)
    }
  })

  test('Passkey login fails without registered passkey', async ({ page }) => {
    // Clean up passkeys
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/profile')
    await page.getByText(/sicherheit|security/i).click()
    await deleteAllPasskeys(page)
    await page.request.post('/api/account/logout')

    const { cdp, authenticatorId } = await addVirtualAuthenticator(page)

    try {
      await page.goto('/login')
      await page.getByRole('button', { name: 'Mit Passkey anmelden' }).click()

      // Should stay on login (no passkey registered = assertion fails)
      await page.waitForTimeout(3_000)
      await expect(page).toHaveURL(/\/login/)
    } finally {
      await removeVirtualAuthenticator(cdp, authenticatorId)
    }
  })

  test('Delete passkey from profile', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/profile')
    await page.getByText(/sicherheit|security/i).click()
    await expect(page.getByRole('heading', { name: /passkey/i })).toBeVisible({ timeout: 10_000 })
    await deleteAllPasskeys(page)

    const { cdp, authenticatorId } = await addVirtualAuthenticator(page)

    try {
      // Register
      await page.getByRole('button', { name: 'Passkey registrieren' }).click()
      await expect(page.getByText(/registriert/)).toBeVisible({ timeout: 10_000 })

      // Delete — the delete button has no title; find it by its trash icon.
      page.on('dialog', dialog => dialog.accept())
      await page.locator('button').filter({ has: page.locator('.lucide-trash-2') }).first().click()

      // Should show "Keine" again
      await expect(page.getByText('Keine')).toBeVisible({ timeout: 5_000 })
    } finally {
      await removeVirtualAuthenticator(cdp, authenticatorId)
    }
  })
})

/** Delete all registered passkeys for the current user via API */
async function deleteAllPasskeys(page: import('@playwright/test').Page) {
  const passkeys = await page.evaluate(async () => {
    const res = await fetch('/api/account/passkey')
    return res.ok ? await res.json() : []
  })
  for (const pk of passkeys) {
    await page.evaluate(async (id) => {
      await fetch(`/api/account/passkey/${id}`, { method: 'DELETE' })
    }, pk.Id)
  }
  // Reload resets the profile to the default (Konto) tab, so navigate back
  // to Security which is where callers were operating.
  await page.reload()
  await page.waitForLoadState('networkidle')
  await page.getByText(/sicherheit|security/i).click()
  await page.getByRole('heading', { name: /passkey/i }).waitFor({ timeout: 10_000 })
}
