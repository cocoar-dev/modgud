import { test, expect } from '@playwright/test'
import { login, apiLoginAndNavigate } from './helpers'

test.describe('Authentication', () => {
  test('Login with valid credentials redirects to dashboard', async ({ page }) => {
    await login(page, 'ka', 'Test1234!')
    await expect(page).toHaveURL(/\/dashboard/)
  })

  test('Login with invalid credentials shows error', async ({ page }) => {
    await page.goto('/login')
    await page.getByRole('textbox', { name: /benutzername|username/i }).fill('ka')
    await page.getByRole('textbox', { name: /passwort|password/i }).fill('WrongPassword!')
    await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()

    // Error message (toast or inline)
    await expect(page.getByText(/ungültig|invalid/i)).toBeVisible({ timeout: 5_000 })
    await expect(page).toHaveURL(/\/login/)
  })

  test('Unauthenticated user is redirected to login or setup', async ({ page }) => {
    await page.goto('/dashboard')
    await expect(page).toHaveURL(/\/(login|setup)/)
  })

  test('Logout redirects to login', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!')
    await page.evaluate(() => fetch('/api/account/logout', { method: 'POST' }))
    await page.goto('/dashboard')
    await expect(page).toHaveURL(/\/login/, { timeout: 10_000 })
  })

  test('Profile page shows user info', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/profile')
    const inputs = page.locator('input[type="text"]')
    await expect(inputs.first()).toBeVisible({ timeout: 10_000 })
    const values = await inputs.evaluateAll((els) => (els as HTMLInputElement[]).map((e) => e.value))
    expect(values).toContain('Test')
    expect(values).toContain('Admin')
  })

  test('Admin routes are accessible for admin users', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/admin/users')
    // Should see the user grid with at least the admin user
    await expect(page.locator('.ag-row').first()).toBeVisible({ timeout: 10_000 })
  })
})
