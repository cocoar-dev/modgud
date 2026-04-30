import { test, expect } from '@playwright/test'
import { apiLogin } from './helpers'
import { clearMailpit, extractQueryParam, waitForMail } from './mailpit'

/**
 * §4 of the manual checklist — profile self-service end-to-end.
 *
 * Drives the full change-request flow against the production-mode
 * container with Mailpit as the real SMTP capture:
 *
 *   1. user PUT /api/account/profile/request — change Firstname (no email
 *      involved) → status `AdminApprovalPending` immediately.
 *   2. user PUT /api/account/profile/request — change Email — verify
 *      Mailpit captures the verification mail, the request stays at
 *      `EmailVerificationPending` until the user clicks the link.
 *   3. anon POST /api/account/profile/request/verify-email with the token
 *      from the mail → status flips to `AdminApprovalPending`.
 *   4. admin POST /api/admin/change-requests/{id}/approve → applies all
 *      pending fields atomically, /me reflects the new values.
 *
 * The change-request aggregate path is covered by
 * `Security/ProfileSelfServiceTests` (9 integration tests). What this E2E
 * adds: the **real SMTP delivery + token round-trip** through Mailpit, and
 * the SPA's behaviour at the /api boundary on the production image.
 */

const ADMIN_USER = 'admin'
const ADMIN_PASSWORD = 'ABC12abc!'
const TEST_PASSWORD = 'TestPass1234!'

const SUFFIX = Math.random().toString(36).slice(2, 8)
const userName = `profile-${SUFFIX}`
const initialEmail = `${userName}-old@cocoar-auth.test`
const newEmail = `${userName}-new@cocoar-auth.test`

test.describe.configure({ mode: 'serial' })

test.beforeAll(async ({ request }) => {
  await clearMailpit()
  // Admin login + create a regular user with a starting email and a known
  // password so the spec can drive the user-side flow.
  const adminLogin = await request.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!adminLogin.ok()) throw new Error('admin login failed')

  const created = await (await request.post('/api/user', {
    data: {
      Firstname: 'Old', Lastname: 'Name', Acronym: userName,
      Email: initialEmail,
    },
  })).json()

  const passRes = await request.put(`/api/user/${created.Id}/password`, {
    data: { Password: TEST_PASSWORD },
  })
  if (!passRes.ok()) throw new Error(`set-password: ${passRes.status()}`)

  await request.post('/api/account/logout')
})

test('non-email field change creates an AdminApprovalPending request', async ({ page }) => {
  await apiLogin(page, userName, TEST_PASSWORD)

  const res = await page.request.put('/api/account/profile/request', {
    data: { Firstname: 'BrandNew' },
  })
  expect(res.ok()).toBeTruthy()
  const body = await res.json()
  expect(body.Open.Status).toBe('AdminApprovalPending')
})

test('email change goes EmailVerificationPending and Mailpit gets the link', async ({ page }) => {
  await apiLogin(page, userName, TEST_PASSWORD)

  // Independent run — drop a fresh Mailpit baseline so the wait below
  // doesn't pick up a stale verification mail from another spec.
  await clearMailpit()
  const before = new Date(Date.now() - 60_000)

  const res = await page.request.put('/api/account/profile/request', {
    data: { Email: newEmail },
  })
  expect(res.ok()).toBeTruthy()
  const body = await res.json()
  expect(body.Open.Status).toBe('EmailVerificationPending')

  // Verification mail goes to the NEW address (so the user has to prove
  // ownership of it before the admin sees the request at all).
  const mail = await waitForMail(newEmail, before, 30_000)
  expect(mail.Subject).toMatch(/verify|bestätig/i)

  // Body carries the verification link with id + token. We pull both
  // straight out — the email shape is the contract.
  const requestId = extractQueryParam(mail.HTML, 'id')
  const token = extractQueryParam(mail.HTML, 'token')

  // Anon POST as if the user clicked the link.
  const verify = await page.request.post('/api/account/profile/request/verify-email', {
    data: { RequestId: requestId, Token: token },
  })
  if (!verify.ok()) {
    throw new Error(`verify-email: ${verify.status()} ${await verify.text()}`)
  }
})

test('admin approves the pending request and the changes apply to the user', async ({ page }) => {
  // Admin context.
  await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)

  // Find the pending request for our user.
  const list = await (await page.request.get('/api/admin/change-requests')).json() as { Id: string; UserId: string; Status: string }[]
  const ours = list.find(r => r.Status === 'AdminApprovalPending')
  expect(ours, `expected an AdminApprovalPending request for ${userName}`).toBeDefined()

  const approve = await page.request.post(`/api/admin/change-requests/${ours!.Id}/approve`, {
    data: { NotifyUser: false },
  })
  if (!approve.ok()) {
    throw new Error(`approve: ${approve.status()} ${await approve.text()}`)
  }

  // Switch back to the user — /me reflects both the new email and the
  // new firstname (atomic apply).
  await apiLogin(page, userName, TEST_PASSWORD)
  const me = await (await page.request.get('/api/account/me')).json()
  expect(me.Firstname).toBe('BrandNew')
  expect(me.Email).toBe(newEmail)
})
