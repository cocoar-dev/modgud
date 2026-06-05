import { test, expect } from '@playwright/test'

/**
 * Stage 3 (login) golden-path — the human door, tested like a human.
 *
 * The plan's Stage-3 centerpiece: sign in with a password through the real UI
 * and land on the dashboard, asserted to Principle 5 of the cold-start ladder
 * (see dev-docs/future-features/human-path-testing-ladder.md):
 *   - real input only — getByRole().fill() / .click() with Playwright's
 *     actionability checks, never synthetic events;
 *   - visibility asserted with toBeVisible(), not DOM presence;
 *   - a screenshot captured at each key step.
 *
 * The admin is bootstrapped up-front by global-setup.ts via the recovery CLI
 * (admin / ABC12abc!). This complements 00-smoke (which checks login via the
 * API) by proving the visible UI journey end to end.
 */

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'

test.describe('Stage 3 — password login golden path', () => {
  test('a human signs in with a password and lands on the dashboard', async ({ page }, testInfo) => {
    // ── 1. The login page is visible and its controls are reachable ──
    await page.goto('/login')

    const userField = page.getByRole('textbox', { name: /benutzername|username/i })
    const passwordField = page.getByRole('textbox', { name: /passwort|password/i })
    const submit = page.getByRole('button', { name: /anmelden|sign in|login/i }).first()

    await expect(userField).toBeVisible()
    await expect(passwordField).toBeVisible()
    await expect(submit).toBeVisible()
    // The submit button is form-gated: disabled until both fields are filled.
    await expect(submit).toBeDisabled()
    await page.screenshot({ path: testInfo.outputPath('01-login.png'), fullPage: true })

    // ── 2. Type the credentials with real input — the button then enables ──
    await userField.fill(ADMIN_USER)
    await passwordField.fill(ADMIN_PASSWORD)
    await expect(submit).toBeEnabled()
    await submit.click()

    // A fresh admin with no 2FA may hit the secure-setup grace modal first;
    // a human would dismiss it to reach the dashboard.
    await Promise.race([
      page.waitForURL(/\/dashboard/, { timeout: 15_000 }),
      page.getByRole('button', { name: /Später|Postpone|Later|Skip/i }).first().waitFor({ timeout: 15_000 }),
    ])
    const skip = page.getByRole('button', { name: /Später|Postpone|Later|Skip/i }).first()
    if (await skip.isVisible().catch(() => false)) {
      await skip.click()
      await page.waitForURL(/\/dashboard/, { timeout: 15_000 })
    }

    // ── 3. We are on the dashboard — visibly, not just per the cookie ──
    await expect(page).toHaveURL(/\/dashboard/)
    // The login form is gone: we left the login page, we didn't just get an
    // inline error.
    await expect(passwordField).toBeHidden()
    await page.screenshot({ path: testInfo.outputPath('02-dashboard.png'), fullPage: true })

    // And the session cookie really authenticates us.
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    expect((await me.json()).UserName).toBe(ADMIN_USER)
  })
})
