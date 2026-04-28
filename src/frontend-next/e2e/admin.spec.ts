import { test, expect } from '@playwright/test'
import { apiLoginAndNavigate } from './helpers'

test.describe('Admin — Roles', () => {
  test.beforeEach(async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/admin/roles')
    // Wait for the create button — it's always there even when the grid is empty.
    // The earlier `.or()` locator tripped strict mode once a real row showed up.
    await expect(page.getByRole('button', { name: /create|erstellen/i }).first()).toBeVisible({ timeout: 10_000 })
  })

  test('Create a role', async ({ page }) => {
    await page.getByRole('button', { name: /create|erstellen/i }).click()
    await expect(page.locator('.modal-header')).toBeVisible({ timeout: 5_000 })

    // Scope the selector to the modal — the grid's search box is also a visible
    // <input>, and first() would otherwise pick that one, leaving Name empty
    // and the submit button disabled.
    await page.locator('.modal-content input').first().fill('E2E Test Role')
    await page.getByRole('button', { name: /create|erstellen/i }).last().click()

    await expect(page.locator('.modal-header')).not.toBeVisible({ timeout: 5_000 })
    await expect(page.locator('.ag-row').filter({ hasText: 'E2E Test Role' })).toBeVisible({ timeout: 5_000 })
  })

  test('Edit a role', async ({ page }) => {
    const row = page.locator('.ag-row').filter({ hasText: 'E2E Test Role' })
    await row.dblclick()
    await expect(page.locator('.modal-header')).toBeVisible({ timeout: 5_000 })

    const nameInput = page.locator('.modal-content input').first()
    await nameInput.clear()
    await nameInput.fill('E2E Updated Role')
    await page.getByRole('button', { name: /save|speichern/i }).click()

    await expect(page.locator('.modal-header')).not.toBeVisible({ timeout: 5_000 })
    await expect(page.locator('.ag-row').filter({ hasText: 'E2E Updated Role' })).toBeVisible({ timeout: 5_000 })
  })
})

test.describe('Admin — Users', () => {
  test.beforeEach(async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/admin/users')
    await expect(page.locator('.ag-row').first()).toBeVisible({ timeout: 10_000 })
  })

  test('User list shows admin user', async ({ page }) => {
    // 'ka' may appear in multiple columns (username, acronym, etc.) — first match is enough.
    await expect(page.getByText('ka').first()).toBeVisible()
  })

  test('Create a user', async ({ page }) => {
    await page.getByRole('button', { name: /create|erstellen/i }).click()
    await expect(page.locator('.modal-header')).toBeVisible({ timeout: 5_000 })

    // Fill required fields — find inputs by their labels/order
    const inputs = page.locator('.modal-content input')
    await inputs.nth(0).fill('E2E')        // Firstname
    await inputs.nth(1).fill('TestUser')    // Lastname
    await inputs.nth(2).fill('E2E')         // Acronym
    await inputs.nth(4).fill('e2euser')     // Username

    await page.getByRole('button', { name: /create|erstellen/i }).last().click()

    await expect(page.locator('.modal-header')).not.toBeVisible({ timeout: 5_000 })
    await expect(page.getByText('e2euser')).toBeVisible({ timeout: 5_000 })
  })

  test('Edit a user', async ({ page }) => {
    const row = page.locator('.ag-row', { hasText: 'e2euser' })
    await row.dblclick()
    await expect(page.locator('.modal-header')).toBeVisible({ timeout: 5_000 })

    // Change firstname
    const firstnameInput = page.locator('.modal-content input').first()
    await firstnameInput.clear()
    await firstnameInput.fill('E2EUpdated')
    await page.getByRole('button', { name: /save|speichern/i }).click()

    await expect(page.locator('.modal-header')).not.toBeVisible({ timeout: 5_000 })
    await expect(page.getByText('E2EUpdated')).toBeVisible({ timeout: 5_000 })
  })
})

