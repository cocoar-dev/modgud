import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test'
import { apiLogin, uniqueSuffix } from './helpers'

/**
 * Stage 5 (user & account management) — the human door, tested like a human.
 *
 * The plan's Stage-5 centerpiece: an admin onboards a new team member through
 * the REAL admin UI — opens the Users grid, clicks Create, fills the form in the
 * routed modal, saves — and the new user appears in the grid (live). Then, to
 * prove the created account is real and not just a row, the admin gives them a
 * password and the new user signs in through the login UI and lands on the
 * dashboard.
 *
 * Asserted to Principle 5 of the cold-start ladder: real input (getByRole().fill
 * / .click()), visibility via toBeVisible(), a screenshot at each key step. This
 * complements 10-admin (which creates users via the API) by proving the visible
 * admin journey end to end.
 */

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'
const NEW_USER_PASSWORD = 'NewMember1234!'

const SUFFIX = uniqueSuffix()

let baseURL: string
test.beforeAll(({ baseURL: b }) => { baseURL = b! })

/** Admin-authenticated API context — used to set the new user's password. */
async function adminContext(): Promise<APIRequestContext> {
  const ctx = await pwRequest.newContext({ baseURL })
  const res = await ctx.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!res.ok()) throw new Error(`admin login failed: ${res.status()} ${await res.text()}`)
  return ctx
}

test.describe('Stage 5 — admin onboards a user through the real UI', () => {
  test('admin creates a user in the Users grid, it appears, and the user signs in', async ({ page }, testInfo) => {
    const newUserName = `member-${SUFFIX}`

    // ── 1. As the admin, open the Users admin grid ──
    await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
    await page.goto('/admin/users')
    await expect(page.getByRole('columnheader', { name: /Benutzername|Username/i }).first())
      .toBeVisible({ timeout: 15_000 })

    // ── 2. Click Create → the routed create-user modal opens ──
    await page.getByRole('button', { name: /erstellen|create/i }).first().click()
    const modal = page.locator('.modal-container')
    await expect(modal).toBeVisible({ timeout: 10_000 })

    // ── 3. Fill the form with real input ──
    await modal.getByRole('textbox', { name: /vorname|first name/i }).fill('New')
    await modal.getByRole('textbox', { name: /nachname|last name/i }).fill('Member')
    await modal.getByRole('textbox', { name: /e-mail|email/i }).fill(`${newUserName}@modgud.test`)
    await modal.getByRole('textbox', { name: /benutzername|username/i }).fill(newUserName)

    // The footer Create button is form-gated: enabled only once the required
    // fields are filled. Target it inside the modal footer so it isn't confused
    // with the grid toolbar's Create button.
    const saveButton = page.locator('.modal-footer').getByRole('button', { name: /erstellen|create/i })
    await expect(saveButton).toBeEnabled()
    await page.screenshot({ path: testInfo.outputPath('01-create-form.png'), fullPage: true })
    await saveButton.click()

    // ── 4. Modal closes and the new user is in the grid — visibly ──
    await expect(modal).toBeHidden({ timeout: 10_000 })
    // The user-grid is fed by an async projection + SignalR; allow it to land.
    await expect(page.getByRole('gridcell', { name: newUserName }).first())
      .toBeVisible({ timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('02-user-in-grid.png'), fullPage: true })

    // ── 5. Give the new user a password (admin action), then they sign in ──
    // A UI-created user starts password-less (Status=Pending); the admin would
    // set one or send a magic-link invite. We set it via the admin API to keep
    // the focus on the create + login UI journey, then look the user up to get
    // its id (the create happened through the UI, so we don't have it yet).
    const admin = await adminContext()
    try {
      // /api/user is an async MultiStreamProjection — the row is in the grid
      // (optimistic store update) a moment before the projection materializes it
      // here, so poll until it lands instead of racing the Marten daemon.
      let created: { Id: string; UserName: string } | undefined
      const deadline = Date.now() + 10_000
      while (Date.now() < deadline) {
        const users = await (await admin.get('/api/user')).json() as Array<{ Id: string; UserName: string }>
        created = users.find(u => u.UserName === newUserName)
        if (created) break
        await new Promise(r => setTimeout(r, 250))
      }
      expect(created, `created user '${newUserName}' present in /api/user`).toBeTruthy()
      const passRes = await admin.put(`/api/user/${created!.Id}/password`, {
        data: { Password: NEW_USER_PASSWORD },
      })
      if (!passRes.ok()) throw new Error(`set-password failed: ${passRes.status()} ${await passRes.text()}`)
    } finally {
      await admin.dispose()
    }

    // ── 6. The new user signs in through the login UI and reaches the dashboard ──
    await page.request.post('/api/account/logout')
    await page.goto('/login')
    await page.getByRole('textbox', { name: /benutzername|username/i }).fill(newUserName)
    await page.getByRole('textbox', { name: /passwort|password/i }).fill(NEW_USER_PASSWORD)
    await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()

    // A fresh user with no 2FA may hit the secure-setup grace modal first.
    const skip = page.getByRole('button', { name: /Später|Postpone|Later|Skip/i }).first()
    await Promise.race([
      page.waitForURL(/\/dashboard/, { timeout: 15_000 }),
      skip.waitFor({ timeout: 15_000 }),
    ])
    if (await skip.isVisible().catch(() => false)) {
      await skip.click()
      await page.waitForURL(/\/dashboard/, { timeout: 15_000 })
    }

    await expect(page).toHaveURL(/\/dashboard/)
    await page.screenshot({ path: testInfo.outputPath('03-new-user-dashboard.png'), fullPage: true })
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    expect((await me.json()).UserName).toBe(newUserName)
  })
})
