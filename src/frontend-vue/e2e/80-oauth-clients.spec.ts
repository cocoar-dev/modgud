import { test, expect } from '@playwright/test'
import { apiLogin, uniqueSuffix } from './helpers'

/**
 * Stage 7 (OAuth / OIDC) — the operator door, tested like a human.
 *
 * The plan's Stage-7 entry point: an admin registers an OAuth client through the
 * REAL admin UI — opens the OAuth-Clients grid, clicks Create, fills the routed
 * modal, saves — sees the one-time client secret reveal (a confidential client's
 * secret is shown exactly once), closes the modal, and the new client appears in
 * the grid. Asserted to Principle 5 (real input, visibility, screenshots).
 *
 * Complements 10-admin §10 (which only checks the list page renders and the API
 * pages) by proving the visible client-registration journey end to end against
 * the production image.
 */

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'
const SUFFIX = uniqueSuffix()

test.describe('Stage 7 — admin registers an OAuth client through the real UI', () => {
  test('admin creates a client, sees the one-time secret, and the client lands in the list', async ({ page }, testInfo) => {
    const clientId = `e2e-client-${SUFFIX}`

    // ── 1. As the admin, open the OAuth-Clients grid ──
    await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
    await page.goto('/admin/oauth/clients')
    await expect(page.getByRole('button', { name: /erstellen|create/i }).first())
      .toBeVisible({ timeout: 15_000 })

    // ── 2. Click Create → the routed create-client modal opens ──
    await page.getByRole('button', { name: /erstellen|create/i }).first().click()
    const modal = page.locator('.modal-container')
    await expect(modal).toBeVisible({ timeout: 10_000 })

    // ── 3. Fill the required Client ID (+ a display name). The create-mode
    //       defaults (Confidential, secret required) make the backend mint a
    //       secret shown exactly once. ──
    await modal.getByRole('textbox', { name: /client id/i }).fill(clientId)
    await modal.getByRole('textbox', { name: /anzeigename|display name/i }).fill('E2E Smoke Client')

    const saveButton = page.locator('.modal-footer').getByRole('button', { name: /erstellen|create/i })
    await expect(saveButton).toBeEnabled()
    await page.screenshot({ path: testInfo.outputPath('01-create-form.png'), fullPage: true })
    await saveButton.click()

    // ── 4. The one-time secret reveal — the human moment ──
    // A confidential client's generated secret is shown once; the modal stays
    // open so the admin can copy it before it's gone for good.
    await expect(page.getByText(/client secret jetzt kopieren|nicht wieder angezeigt|copy.*client secret|not be shown again/i))
      .toBeVisible({ timeout: 10_000 })
    await page.screenshot({ path: testInfo.outputPath('02-secret-once.png'), fullPage: true })

    // ── 5. Close the modal; the new client is in the grid — visibly ──
    await page.locator('.modal-close').click()
    await expect(modal).toBeHidden({ timeout: 10_000 })
    await expect(page.getByRole('gridcell', { name: clientId }).first())
      .toBeVisible({ timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('03-client-in-grid.png'), fullPage: true })

    // ── 6. And it's a real client on the API ──
    const list = await page.request.get('/api/admin/oauth/clients')
    expect(list.ok()).toBeTruthy()
    const body = await list.json() as { Items: Array<{ ClientId: string }> }
    expect(body.Items.some(c => c.ClientId === clientId)).toBeTruthy()
  })
})
