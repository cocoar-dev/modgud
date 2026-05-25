import { test, expect } from '@playwright/test'
import { apiLogin, login } from './helpers'
import { clearMailpit, extractQueryParam, extractTokenFromHtml, waitForMail } from './mailpit'

/**
 * Phase A of the manual-checklist port. One spec covering §0 + §1 + §2 of
 * `docs/testing/manual-checklist.md` end-to-end against the **production
 * shipping image** (no Development-mode tricks). Mailpit captures outbound
 * SMTP so the magic-link flow can be verified for real.
 *
 * Tests that overlap with already-pinned integration coverage are not
 * re-run here:
 *   - Brute-force lockout → `OwaspTop10Tests.A07_BruteForce_Locks_Account_After_Configured_Failures`
 *   - User-existence-leak → `OwaspTop10Tests.A02_Login_Does_Not_Reveal_User_Existence`
 *     and `A07_ForgotPassword_Always_Returns_200`
 *
 * The E2E layer is the place for things integration tests can't see
 * end-to-end: the SPA wiring, the real SMTP path through SmtpEmailService,
 * cookie + redirect behaviour, and SignalR live updates.
 *
 * Tests run in file-declaration order under workers=1 (set in
 * playwright.config.ts), so they form a sequence. Admin is created
 * up-front by `global-setup.ts` via the recovery CLI (the anonymous
 * /setup wizard was removed in the C15 reform), then downstream tests
 * reuse those credentials.
 */

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'
const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'admin@modgud.test'

test.beforeAll(async () => {
  await clearMailpit()
})

test.describe('§1 First admin (bootstrapped pre-test)', () => {
  test('admin from recovery-CLI bootstrap can log in and carries realm:admin', async ({ page }) => {
    await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    const body = await me.json()
    expect(body.UserName).toBe(ADMIN_USER)
    expect(body.Permissions).toContain('realm:admin')
  })
})

test.describe('§2 Login & sign-out', () => {
  test.beforeEach(async () => {
    await clearMailpit()
  })

  test('correct password completes login', async ({ page }) => {
    await login(page, ADMIN_USER, ADMIN_PASSWORD)
    // The auth cookie is the source of truth for "logged in". /me only
    // returns 200 with our user when the cookie is set.
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    expect((await me.json()).UserName).toBe(ADMIN_USER)
  })

  test('sign-out clears the cookie and lands on /login', async ({ page }) => {
    await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
    await page.goto('/dashboard')

    // Open the user-menu in the header and trigger sign-out.
    await page.getByRole('button', { name: /^AD$|admin/i }).first().click()
    await page.getByRole('menuitem', { name: /Abmelden|Sign out|Logout/i }).click()
    await page.waitForURL(/\/login/, { timeout: 10_000 })

    // Cookie should be gone.
    const me = await page.request.get('/api/account/me')
    expect(me.status()).toBe(401)
  })

  test('magic-link request lands a real mail in Mailpit and the token signs the user in', async ({ page, request }) => {
    // Setup wizard didn't ask for an email, so attach one to the admin
    // first — Marten user → admin update via the admin user API.
    await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
    const me = await (await page.request.get('/api/account/me')).json()

    // OptionalJsonConverterFactory deserialises the bare value into
    // Optional<string>.HasValue=true — so the wire shape is just
    // `{ "Email": "..." }`, not a `{HasValue, Value}` envelope.
    const updateRes = await page.request.put(`/api/user/${me.Id}`, {
      data: { Email: ADMIN_EMAIL },
    })
    if (!updateRes.ok()) {
      throw new Error(`PUT /api/user/${me.Id} failed: ${updateRes.status()} ${await updateRes.text()}`)
    }

    // Sign out + request magic link.
    await page.request.post('/api/account/logout')
    // Wide window — clock skew between the test process and the container
    // can drop a freshly-arrived mail under a strict cutoff. beforeEach
    // already cleared Mailpit so anything in there is from this test.
    const before = new Date(Date.now() - 60_000)
    const requested = await request.post('/api/account/magic-link/request', {
      data: { Email: ADMIN_EMAIL },
    })
    if (!requested.ok()) {
      throw new Error(`magic-link request failed: ${requested.status()} ${await requested.text()}`)
    }

    // Real SMTP path → mailpit caught the mail. 30s — outbound SMTP +
    // queue → Marten persistence + AuthLog can take a moment.
    const mail = await waitForMail(ADMIN_EMAIL, before, 30_000)
    expect(mail.Subject).toMatch(/login|magic|anmelde|sign in/i)
    const token = extractTokenFromHtml(mail.HTML)
    expect(token.length).toBeGreaterThan(8)

    // The login endpoint takes a real `Guid UserId`, not the ShortGuid form
    // that /api/account/me returns. Mail link encodes the canonical Guid as
    // `?userId=...`, so we lift it straight out instead of round-tripping
    // through ShortGuid → Guid client-side.
    const userIdFromLink = extractQueryParam(mail.HTML, 'userId')
    const linkLogin = await request.post('/api/account/magic-link/login', {
      data: { UserId: userIdFromLink, Token: token },
    })
    if (!linkLogin.ok()) {
      throw new Error(`magic-link login failed: ${linkLogin.status()} ${await linkLogin.text()}`)
    }
  })
})
