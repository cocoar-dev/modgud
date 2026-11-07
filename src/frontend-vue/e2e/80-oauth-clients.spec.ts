import { test, expect } from '@playwright/test'
import { applyStagedDraft, apiLogin, STAGE_INTO_DRAFT, uniqueSuffix } from './helpers'

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
    await expect(page.getByRole('button', { name: /^(erstellen|create)$/i }).filter({ visible: true }).first())
      .toBeVisible({ timeout: 15_000 })

    // ── 2. Click Create → the routed create-client modal opens ──
    await page.getByRole('button', { name: /^(erstellen|create)$/i }).filter({ visible: true }).first().click()
    const modal = page.locator('.modal-container')
    await expect(modal).toBeVisible({ timeout: 10_000 })

    // ── 3. Fill the required Client ID (+ a display name). The create-mode
    //       defaults (Confidential, secret required) make the backend mint a
    //       secret shown exactly once. ──
    await modal.getByRole('textbox', { name: /client id/i }).fill(clientId)
    await modal.getByRole('textbox', { name: /anzeigename|display name/i }).fill('E2E Smoke Client')

    // A client needs at least one flow. device_code needs nothing else (authorization_code
    // would also need a redirect URI); double-click moves it to the selected list.
    await modal.getByRole('tab', { name: /^flows\b/i }).click()
    await modal.getByRole('option', { name: /^device_code/i }).dblclick()

    // A realm admin stages the client into the realm's configuration draft.
    const saveButton = modal.getByRole('button', { name: STAGE_INTO_DRAFT })
    await expect(saveButton).toBeEnabled()
    await page.screenshot({ path: testInfo.outputPath('01-create-form.png'), fullPage: true })
    await saveButton.click()
    await expect(modal).toBeHidden({ timeout: 10_000 })

    // ── 4. Apply the draft; the one-time secret reveal — the human moment ──
    // Applying a draft that minted a confidential client's secret leads to the draft
    // workspace, which shows each new secret exactly once.
    await applyStagedDraft(page)
    await expect(page.getByText(/werden nur einmal angezeigt|shown only once/i)).toBeVisible({ timeout: 10_000 })
    await expect(page.locator('.secret-client', { hasText: clientId })).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath('02-secret-once.png'), fullPage: true })

    // ── 5. The new client is in the grid — visibly ──
    await page.goto('/admin/oauth/clients')
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
