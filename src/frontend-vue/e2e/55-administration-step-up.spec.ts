import { test, expect, type Page } from '@playwright/test'
import { apiLogin, uniqueSuffix } from './helpers'

/**
 * ADR 0025 — the portal opens at the realm's sign-in level, the administration
 * (`/api/admin/*`) may ask for a higher one. An administrator who signed in to Modgud
 * with one factor used to see the dashboard for a moment and was then pulled to the login
 * page: a dashboard widget read the security log from the administration, the request was
 * refused, and the HTTP client answered the refusal with a redirect.
 *
 * Two rules replace that (amendment 2):
 *   - an administrator signing in to Modgud itself is held to the administration's level
 *     in that sign-in — the first spec;
 *   - a session that is below it anyway (signed in for an App, then opening Modgud) keeps
 *     the portal: `/api/account/me` says that the administration would ask
 *     (`AdministrationNeedsStepUp`), the page asks before it goes there and loads nothing
 *     from there into the dashboard — the other two.
 *
 * The user here is a realm administrator without a second factor and with a setup grace
 * of zero days, so the administration is closed to them until they set one up. The same
 * holds for an administrator who has a passkey and signed in with a password — the rig
 * cannot run a passkey ceremony, see 06-passwordless-login.
 */

const ADMIN_USER = 'admin'
const ADMIN_PASSWORD = 'ABC12abc!'
const TEST_PASSWORD = 'TestPass1234!'

const userName = `stepup-${uniqueSuffix()}`
const email = `${userName}@modgud.test`

const LOCKED = /Gehört zur Administration|Part of the administration/i

test.describe.configure({ mode: 'serial' })

test.beforeAll(async ({ request }) => {
  const adminLogin = await request.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!adminLogin.ok()) throw new Error('admin login failed')

  const createRes = await request.post('/api/user', {
    data: {
      Firstname: 'Step', Lastname: 'Up', Acronym: userName, Email: email,
      EmailConfirmed: true, GracePeriodDaysOverride: 0,
    },
  })
  if (!createRes.ok()) throw new Error(`create user: ${createRes.status()} ${await createRes.text()}`)
  const user = await createRes.json() as { Id: string }
  const passRes = await request.put(`/api/user/${user.Id}/password`, { data: { Password: TEST_PASSWORD } })
  if (!passRes.ok()) throw new Error(`set-password: ${passRes.status()} ${await passRes.text()}`)

  // The realm's administrator role, granted through a group: this user sees every widget
  // and every sidebar entry, so nothing on the dashboard may reach for the administration.
  const roleRes = await request.post('/api/role', {
    data: { Name: `Role-${userName}`, IsRealmAdmin: true, PermissionIds: [] },
  })
  if (!roleRes.ok()) throw new Error(`create role: ${roleRes.status()} ${await roleRes.text()}`)
  const role = await roleRes.json() as { Id: string }
  const groupRes = await request.post('/api/group', {
    data: {
      Name: `Group-${userName}`, MemberIds: [user.Id], RoleIds: [role.Id],
      MembershipMode: 'Manual', EmailMode: 'Shared', BoundTo: ['modgud'],
    },
  })
  if (!groupRes.ok()) throw new Error(`create group: ${groupRes.status()} ${await groupRes.text()}`)

  await request.post('/api/account/logout')
})

/** Every request the page sends to the administration. */
function administrationRequests(page: Page): string[] {
  const seen: string[] = []
  page.on('request', (r) => {
    if (new URL(r.url()).pathname.startsWith('/api/admin/')) seen.push(r.url())
  })
  return seen
}

test('an administrator signing in to Modgud is asked before anything is shown', async ({ page }) => {
  const toAdministration = administrationRequests(page)
  const visited: string[] = []
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) visited.push(new URL(frame.url()).pathname)
  })

  await page.goto('/login')
  await page.getByRole('textbox', { name: /benutzername|username/i }).fill(email)
  await page.getByRole('textbox', { name: /passwort|password/i }).fill(TEST_PASSWORD)
  await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()

  // The setup of the missing factor, on the login page — required, not postponable.
  await expect(page.getByText(/Authenticator.App/i).first()).toBeVisible()
  await expect(page.getByRole('button', { name: /Später|Postpone|Later|Skip/i })).toHaveCount(0)
  await page.waitForLoadState('networkidle')

  expect(visited.filter((path) => path !== '/login')).toEqual([])
  expect(toAdministration).toEqual([])
})

// The session of the next two specs is signed in but below the administration's level, as
// a sign-in for an App leaves an administrator's session.
test('the dashboard stays open and asks instead of loading administration data', async ({ page }) => {
  const toAdministration = administrationRequests(page)
  await apiLogin(page, email, TEST_PASSWORD)

  await page.goto('/dashboard')
  await expect(page.getByText(LOCKED)).toBeVisible()
  await page.waitForLoadState('networkidle')

  expect(new URL(page.url()).pathname).toBe('/dashboard')
  expect(toAdministration).toEqual([])

  // The widget's button is the way to the missing factor, and back to the dashboard.
  await page.getByRole('button', { name: /^(Bestätigen|Confirm)$/ }).click()
  await page.waitForURL((url) => url.pathname === '/login'
    && url.searchParams.get('for') === 'admin'
    && url.searchParams.get('redirect') === '/dashboard')
  expect(toAdministration).toEqual([])
})

test('the administration asks before its page opens', async ({ page }) => {
  const toAdministration = administrationRequests(page)
  await apiLogin(page, email, TEST_PASSWORD)

  await page.goto('/admin/users')
  await page.waitForURL((url) => url.pathname === '/login'
    && url.searchParams.get('for') === 'admin'
    && url.searchParams.get('redirect') === '/admin/users')

  // Asked first: nothing was requested from the administration to find that out.
  expect(toAdministration).toEqual([])
})
