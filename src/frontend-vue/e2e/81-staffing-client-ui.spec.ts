import { test, expect } from '@playwright/test'
import { apiLogin } from './helpers'

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'

/**
 * MG-FT-FLEX — verify the create-client editor as an operator actually sees it.
 *
 * This test deliberately stops before Create: it exercises the complete local
 * form state without leaving an OAuth client, Position, or terminal slot behind.
 * A one-off local run may set E2E_EPHEMERAL_ADMIN=true; in that mode the
 * bootstrap account used for the run is permanently erased in finally.
 */
test('staffing is an exclusive grant with a compact, dedicated terminal profile', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)

  const meResponse = await page.request.get('/api/account/me')
  expect(meResponse.ok()).toBeTruthy()
  const me = await meResponse.json() as { Id: string }

  try {
    await page.goto('/admin/oauth/clients#create')

    const modal = page.locator('.modal-container')
    await expect(modal).toBeVisible({ timeout: 15_000 })
    await expect(modal.getByText('OAuth-Client erstellen', { exact: true })).toBeVisible()

    // Identity remains operator-owned when the Staffing profile is selected.
    const clientId = modal.getByRole('textbox', { name: /client id/i })
    const displayName = modal.getByRole('textbox', { name: /display name/i })
    await expect(clientId).toBeEnabled()
    await expect(displayName).toBeEnabled()
    await clientId.fill('staffing-ui-smoke')
    await displayName.fill('Staffing UI Smoke')

    await modal.getByRole('tab', { name: /^flows\b/i }).click()
    const staffing = modal.getByRole('option', { name: /staffing/i }).first()
    await expect(staffing).toBeVisible()
    await staffing.dblclick()

    // Selecting Staffing adds exactly one compact destination for its required
    // metadata instead of growing the Flows tab vertically.
    const terminalTab = modal.getByRole('tab', { name: /^terminal/i })
    await expect(terminalTab).toBeVisible()
    await expect(modal.getByText(/zugehörige position/i)).toBeHidden()
    await expect(modal.getByText(/terminalname/i)).toBeHidden()
    await expect(modal.getByText(/konfiguration unvollständig \(0\)/i)).toHaveCount(0)
    await page.screenshot({ path: testInfo.outputPath('01-staffing-flow.png'), fullPage: true })

    // A mixed grant remains selectable for diagnosis, but is visibly invalid
    // and Create stays blocked. Removing it restores the exclusive profile.
    const authorizationCode = modal.getByRole('option', { name: /^authorization_code/i }).first()
    await authorizationCode.dblclick()
    await expect(modal.getByText(/konfiguration unvollständig \(1\)/i)).toBeVisible()
    await expect(modal.locator('.modal-footer').getByRole('button', { name: /erstellen/i })).toBeDisabled()
    await modal.getByRole('option', { name: /^authorization_code/i }).last().dblclick()

    await modal.getByRole('tab', { name: /^allgemein$/i }).click()
    await expect(modal.getByText(/staffing legt client-typ und aktivstatus fest/i)).toBeVisible()
    await expect(clientId).toBeEnabled()
    await expect(displayName).toBeEnabled()
    await expect(modal.getByRole('combobox', { name: /client-typ/i })).toBeDisabled()

    await modal.getByRole('tab', { name: /login & zustimmung/i }).click()
    await expect(modal.getByText(/staffing verwendet implizite zustimmung/i)).toBeVisible()
    await expect(modal.getByRole('textbox', { name: /webauthn rp-id/i })).toBeEnabled()

    await terminalTab.click()
    await expect(modal.getByText(/konfiguration unvollständig \(3\)/i)).toBeVisible()
    await expect(modal.getByText(/zugehörige position/i)).toBeVisible()
    await expect(modal.getByRole('button', { name: /neu anlegen/i })).toBeVisible()
    await expect(modal.getByRole('textbox', { name: /terminalname/i })).toBeVisible()
    await expect(modal.getByRole('textbox', { name: /standort/i })).toBeVisible()
    await expect(modal.getByRole('textbox', { name: /^webauthn rp-id$/i })).toBeVisible()
    await expect(modal.getByRole('combobox', { name: /gerätebindung/i })).toBeVisible()
    await expect(modal.getByText(/position, terminal-slot und oauth-client werden gemeinsam erstellt/i)).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath('02-terminal-profile.png'), fullPage: true })
  } finally {
    if (process.env.E2E_EPHEMERAL_ADMIN === 'true') {
      const cleanup = await page.request.delete(`/api/admin/users/${me.Id}/permanent`, {
        data: { Reason: 'Temporary MG-FT-FLEX UI smoke account cleanup' },
      })
      expect(cleanup.status(), await cleanup.text()).toBe(204)
    }
  }
})
