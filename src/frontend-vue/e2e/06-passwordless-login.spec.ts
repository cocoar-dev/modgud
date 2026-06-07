import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test'
import { generateSync } from 'otplib'
import { fillOtpCode, uniqueSuffix } from './helpers'
import { clearMailpit, extractOtpCodeFromHtml, waitForMail } from './mailpit'

/**
 * Stage 3 (login) — the passwordless / second-factor sub-gates, each driven
 * through the REAL login UI (the golden-path in 05-* covers the password door;
 * this file covers the rest of the human doors).
 *
 * Asserted to Principle 5 of the cold-start ladder
 * (dev-docs/future-features/human-path-testing-ladder.md): real input only
 * (getByRole().fill()/.click() + real keystrokes via fillOtpCode), visibility
 * via toBeVisible(), and a screenshot at each key step. Email flows are
 * observed through the real SMTP path captured by Mailpit — no Development-mode
 * /api/dev/* shortcuts (the rig runs the production image in Staging).
 *
 * Setup that isn't itself the gate under test (creating the user, enabling the
 * factor) goes through the authenticated API; what the UI test actually drives
 * is the LOGIN journey for each method. Each method uses its own freshly-minted
 * user so enabling a second factor never bleeds into the password golden-path
 * or the admin-only specs (workers=1, shared backend state).
 *
 * Passkey is the one method that cannot be exercised here — see the skipped
 * describe block at the bottom for why and where it stays covered.
 */

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'
const TEST_PASSWORD = 'TestPass1234!'

// Per-spec unique suffix so re-runs against a reused container never collide.
function uniqueName(prefix: string): string {
  return `${prefix}-${uniqueSuffix()}`
}

/** Admin-authenticated API context — used only to provision test users. */
async function adminContext(baseURL: string): Promise<APIRequestContext> {
  const ctx = await pwRequest.newContext({ baseURL })
  const res = await ctx.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!res.ok()) throw new Error(`admin login failed: ${res.status()} ${await res.text()}`)
  return ctx
}

/** A user-authenticated API context — for self-service setup (enable a factor). */
async function userContext(baseURL: string, userName: string, password: string): Promise<APIRequestContext> {
  const ctx = await pwRequest.newContext({ baseURL })
  const res = await ctx.post('/api/account/login', {
    data: { UserName: userName, Password: password, RememberMe: false },
  })
  if (!res.ok()) throw new Error(`user login failed for ${userName}: ${res.status()} ${await res.text()}`)
  return ctx
}

interface TestUser { id: string; userName: string; email: string }

/**
 * Create a test user (username == acronym, mirroring 50-2fa) with a password.
 * `emailConfirmed` is the admin opt-in (UserCreateDto.EmailConfirmed) — needed
 * for Email-OTP, whose enable endpoint refuses an unverified address by design.
 */
async function createUser(admin: APIRequestContext, opts: { emailConfirmed?: boolean } = {}): Promise<TestUser> {
  const userName = uniqueName('pw')
  const email = `${userName}@modgud.test`
  const createRes = await admin.post('/api/user', {
    data: {
      Firstname: 'PW', Lastname: 'User', Acronym: userName, Email: email,
      EmailConfirmed: opts.emailConfirmed ?? false,
    },
  })
  if (!createRes.ok()) throw new Error(`create user: ${createRes.status()} ${await createRes.text()}`)
  const created = await createRes.json()
  const passRes = await admin.put(`/api/user/${created.Id}/password`, { data: { Password: TEST_PASSWORD } })
  if (!passRes.ok()) throw new Error(`set-password: ${passRes.status()} ${await passRes.text()}`)
  return { id: created.Id as string, userName, email }
}

/** Fill the credentials step of the login form and submit it. */
async function submitCredentials(page: import('@playwright/test').Page, userName: string, password: string) {
  await page.goto('/login')
  await page.getByRole('textbox', { name: /benutzername|username/i }).fill(userName)
  await page.getByRole('textbox', { name: /passwort|password/i }).fill(password)
  await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()
}

let baseURL: string
test.beforeAll(({ baseURL: b }) => { baseURL = b! })

// ────────────────────────────────────────────────────────────────────
// Magic link — passwordless login by clicking an emailed link
// ────────────────────────────────────────────────────────────────────
test.describe('Stage 3 — magic-link login via the real UI', () => {
  test('a human requests a login link, opens it from the mail, and lands signed in', async ({ page }, testInfo) => {
    const admin = await adminContext(baseURL)
    let user: TestUser
    try {
      user = await createUser(admin, { emailConfirmed: true })
    } finally {
      await admin.dispose()
    }

    await clearMailpit()
    const before = new Date(Date.now() - 60_000)

    // ── 1. From the login page, switch to the magic-link form ──
    await page.goto('/login')
    const magicEntry = page.getByRole('button', { name: /login link via email|anmelde-link per e-mail/i })
    await expect(magicEntry).toBeVisible()
    await magicEntry.click()

    // ── 2. Enter the email; the send button is form-gated until it's filled ──
    const emailField = page.getByPlaceholder(/email@/i)
    await expect(emailField).toBeVisible()
    const sendBtn = page.getByRole('button', { name: /send link|link senden/i })
    await expect(sendBtn).toBeDisabled()
    await emailField.fill(user.email)
    await expect(sendBtn).toBeEnabled()
    await page.screenshot({ path: testInfo.outputPath('01-magic-request.png'), fullPage: true })
    await sendBtn.click()

    // Anti-enumeration: a success note shows whether or not the account exists.
    await expect(page.getByText(/login link was sent|anmelde-link.*gesendet|check your inbox|posteingang/i))
      .toBeVisible({ timeout: 10_000 })

    // ── 3. The real mail reached Mailpit; open its link like a human would ──
    const mail = await waitForMail(user.email, before, 30_000)
    const href = mail.HTML.match(/href="([^"]*magic-login[^"]*)"/)?.[1]
    expect(href, 'magic-login link present in the mail body').toBeTruthy()
    const linkUrl = new URL(href!.replace(/&amp;/g, '&'))
    await page.goto(`${linkUrl.pathname}${linkUrl.search}`)

    // ── 4. Signed in as the link's owner, off the login pages ──
    await page.waitForURL((u) => !u.pathname.includes('/login') && !u.pathname.includes('/magic-login'), { timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('02-signed-in.png'), fullPage: true })
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    expect((await me.json()).UserName).toBe(user.userName)
  })
})

// ────────────────────────────────────────────────────────────────────
// Email-OTP — a 6-digit code mailed on each login
// ────────────────────────────────────────────────────────────────────
test.describe('Stage 3 — email-OTP second factor via the real UI', () => {
  test('password login prompts for an emailed code, and entering it completes sign-in', async ({ page }, testInfo) => {
    const admin = await adminContext(baseURL)
    let user: TestUser
    try {
      user = await createUser(admin, { emailConfirmed: true })
    } finally {
      await admin.dispose()
    }

    // Enable Email-OTP for the user (self-service API; the gate under test is
    // the LOGIN flow, not the profile toggle). Requires a confirmed email,
    // which createUser set via the admin opt-in.
    const uctx = await userContext(baseURL, user.userName, TEST_PASSWORD)
    try {
      const enable = await uctx.post('/api/account/email-otp/enable')
      if (!enable.ok()) throw new Error(`email-otp enable: ${enable.status()} ${await enable.text()}`)
    } finally {
      await uctx.dispose()
    }

    await clearMailpit()
    const before = new Date(Date.now() - 60_000)

    // ── 1. Password login → the email-OTP step (the code is auto-sent) ──
    await submitCredentials(page, user.userName, TEST_PASSWORD)
    await expect(page.getByText(/code was sent to your email|code wurde an ihre e-mail/i))
      .toBeVisible({ timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('01-otp-prompt.png'), fullPage: true })

    // ── 2. Read the real code from Mailpit and type it like a human ──
    const mail = await waitForMail(user.email, before, 30_000)
    const code = extractOtpCodeFromHtml(mail.HTML)
    expect(code).toMatch(/^\d{6}$/)
    await fillOtpCode(page, code)

    const confirm = page.getByRole('button', { name: /^confirm$|bestätigen/i })
    await expect(confirm).toBeEnabled()
    await confirm.click()

    // ── 3. Signed in ──
    await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('02-signed-in.png'), fullPage: true })
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    expect((await me.json()).UserName).toBe(user.userName)
  })
})

// ────────────────────────────────────────────────────────────────────
// TOTP — authenticator-app second factor
// ────────────────────────────────────────────────────────────────────
test.describe('Stage 3 — TOTP second factor via the real UI', () => {
  test('password login prompts for an authenticator code, and entering it completes sign-in', async ({ page }, testInfo) => {
    const admin = await adminContext(baseURL)
    let user: TestUser
    try {
      user = await createUser(admin)
    } finally {
      await admin.dispose()
    }

    // Enable TOTP via the authenticated API (setup + verify, mirroring 50-2fa).
    // otplib generates the same RFC-6238 codes ASP.NET Identity validates.
    const uctx = await userContext(baseURL, user.userName, TEST_PASSWORD)
    let secret: string
    try {
      const setup = await (await uctx.post('/api/account/mfa/setup')).json() as { SharedKey: string }
      secret = setup.SharedKey.replace(/\s+/g, '')
      expect(secret.length).toBeGreaterThan(0)
      const verify = await uctx.post('/api/account/mfa/verify', { data: { Code: generateSync({ secret }) } })
      if (!verify.ok()) throw new Error(`mfa verify: ${verify.status()} ${await verify.text()}`)
    } finally {
      await uctx.dispose()
    }

    // ── 1. Password login → the TOTP step ──
    await submitCredentials(page, user.userName, TEST_PASSWORD)
    await expect(page.getByText(/code from your authenticator app|code aus ihrer authenticator-app/i))
      .toBeVisible({ timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('01-totp-prompt.png'), fullPage: true })

    // ── 2. Generate a fresh code and type it ──
    await fillOtpCode(page, generateSync({ secret }))
    const confirm = page.getByRole('button', { name: /^confirm$|bestätigen/i })
    await expect(confirm).toBeEnabled()
    await confirm.click()

    // ── 3. Signed in ──
    await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('02-signed-in.png'), fullPage: true })
    const me = await page.request.get('/api/account/me')
    expect(me.ok()).toBeTruthy()
    const meJson = await me.json()
    expect(meJson.UserName).toBe(user.userName)
    expect(meJson.Has2FA).toBe(true)
  })
})

// ────────────────────────────────────────────────────────────────────
// Passkey — not reachable end-to-end against this rig (documented gap)
// ────────────────────────────────────────────────────────────────────
test.describe('Stage 3 — passkey login via the real UI', () => {
  // Passkey (WebAuthn) cannot be driven end-to-end against THIS rig, and that's
  // a property of the rig, not a missing test. RealmFido2.BuildConfiguration
  // (Modgud.Authentication/Identity/RealmFido2.cs) allows the relying-party
  // origin `https://{realm.PrimaryDomain}` only, outside Development — and a
  // realm with no PrimaryDomain returns 503 ("Passkey.Unavailable") by design.
  // The production-parity rig serves plain HTTP on http://localhost:14200, so
  // the browser's ceremony origin can never match the relying party's allowed
  // origins; a CDP virtual-authenticator assertion is rejected before it can
  // complete, whatever PrimaryDomain we set.
  //
  // Passkey therefore stays covered at the integration layer:
  //   - PasskeyLoginTests — the happy-path ceremony, and
  //   - the Stage-3 backend 503 "Passkey.Unavailable" graceful-failure test.
  //
  // Re-enable this once the rig can serve HTTPS on the realm's PrimaryDomain
  // (e.g. a self-signed cert + a host the origin set accepts).
  test.fixme('register a passkey in the profile and sign in with it', async () => {
    // Intentionally empty: see the block comment above. Marked fixme so the
    // gap is visible in the report instead of silently absent.
  })
})
