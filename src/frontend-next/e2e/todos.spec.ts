import { test, expect } from '@playwright/test'
import { apiLoginAndNavigate } from './helpers'

test.describe('Todos', () => {
  test.beforeEach(async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/todos')
    await expect(page.locator('.ag-root-wrapper')).toBeVisible({ timeout: 10_000 })
  })

  test('Todo grid loads', async ({ page }) => {
    await expect(page.locator('.ag-header')).toBeVisible()
  })

  test('Create a todo', async ({ page }) => {
    await page.getByRole('button', { name: /create|erstellen/i }).click()
    await expect(page.locator('.modal-header')).toBeVisible({ timeout: 5_000 })

    // Fill title
    await page.locator('.modal-content input').first().fill('E2E Test Todo')
    await page.getByRole('button', { name: /create|erstellen/i }).last().click()

    await expect(page.locator('.modal-header')).not.toBeVisible({ timeout: 5_000 })
    // AG-Grid's tree-cell inner span is display:none for non-leaf cells, so the
    // text locator hits a hidden element. Assert on the row wrapper instead.
    await expect(page.locator('.ag-row').filter({ hasText: 'E2E Test Todo' })).toBeVisible({ timeout: 10_000 })
  })

  test('Edit a todo via double-click', async ({ page }) => {
    const row = page.locator('.ag-row').filter({ hasText: 'E2E Test Todo' })
    await row.dblclick()
    await expect(page.locator('.modal-header')).toBeVisible({ timeout: 5_000 })

    // Change title
    const titleInput = page.locator('.modal-content input').first()
    await expect(titleInput).toHaveValue('E2E Test Todo')
    await titleInput.clear()
    await titleInput.fill('E2E Todo Updated')
    await page.getByRole('button', { name: /save|speichern/i }).click()

    await expect(page.locator('.modal-header')).not.toBeVisible({ timeout: 5_000 })
    await expect(page.locator('.ag-row').filter({ hasText: 'E2E Todo Updated' })).toBeVisible({ timeout: 10_000 })
  })

  test('Double-click on comments column opens comments tab', async ({ page }) => {
    // First create a todo with a known name
    const row = page.locator('.ag-row').filter({ hasText: 'E2E Todo Updated' })
    // Double-click specifically on the comments cell
    const commentsCell = row.locator('[col-id="CommentsCount"]')
    await commentsCell.dblclick()

    await expect(page.locator('.modal-header')).toBeVisible({ timeout: 5_000 })
    // Comments tab should be active
    await expect(page.locator('[id="comments"][aria-selected="true"], .coar-tab--active')).toBeVisible({ timeout: 3_000 })
  })
})

test.describe('Dashboard', () => {
  test('Dashboard loads with KPI cards', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/dashboard')

    // KPI cards should be visible
    await expect(page.locator('.kpi-card').first()).toBeVisible({ timeout: 10_000 })
  })

  test('Clicking a KPI card shows filter grid', async ({ page }) => {
    await apiLoginAndNavigate(page, 'ka', 'Test1234!', '/dashboard')
    await expect(page.locator('.kpi-card').first()).toBeVisible({ timeout: 10_000 })

    // Click first card
    await page.locator('.kpi-card').first().click()

    // Grid or "no results" message should appear (use .first() — the filter view
    // might render multiple "no results" hints simultaneously across KPI sections).
    await expect(
      page.locator('.ag-root-wrapper').or(page.getByText(/keine|no tasks/i)).first()
    ).toBeVisible({ timeout: 5_000 })
  })
})
