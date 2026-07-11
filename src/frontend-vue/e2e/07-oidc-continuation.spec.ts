import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test'
import { createHash, randomBytes } from 'node:crypto'
import { uniqueSuffix } from './helpers'
import { clearMailpit, waitForMail } from './mailpit'

/**
 * OIDC continuation — a client app's /connect/authorize flow must survive
 * every path through the login SPA and hand control BACK to the client.
 *
 * Regression coverage for the 2026-07-10 login-redirect bug report: the
 * pending authorize continuation rides `?redirect=` (cookie handler's
 * ReturnUrlParameter) and used to be dropped by mid-flow detours, the
 * magic-link e-mail round trip, and the consent deny/dead-ticket paths —
 * stranding users on the IdP dashboard while the client waited forever.
 *
 * Two journeys, both entered like a real client app (unauthenticated
 * /connect/authorize with PKCE):
 *
 *  1. challenge → login page (detour links forward ?redirect=) → password
 *     login resumes authorize → consent → DENY returns the RFC 6749
 *     `error=access_denied` (+ state) to the client's redirect_uri.
 *  2. challenge → magic-link request (continuation rides the emailed URL
 *     as ?redirect=) → opening the mail's link signs in AND resumes
 *     authorize → consent → ALLOW lands ?code= on the client.
 */

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'
const TEST_PASSWORD = 'TestPass1234!'

// Unique-per-run names so re-runs against a reused container never collide.
const SUFFIX = uniqueSuffix()
const CLIENT_ID = `pw-cont-${SUFFIX}`
// Loopback redirect — nothing is listening; the navigation never commits, so
// tests capture the *issued* request (fires before the connection refuses).
const REDIRECT_URI = 'http://localhost/pw-cont-cb'

let baseURL: string
test.beforeAll(({ baseURL: b }) => { baseURL = b! })

/** Admin-authenticated API context — used only for provisioning. */
async function adminContext(): Promise<APIRequestContext> {
  const ctx = await pwRequest.newContext({ baseURL })
  const res = await ctx.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!res.ok()) throw new Error(`admin login failed: ${res.status()} ${await res.text()}`)
  return ctx
}

/** Explicit-consent public PKCE client — the shape a BFF/SPA relying party uses. */
async function createExplicitClient(admin: APIRequestContext): Promise<void> {
  const res = await admin.post('/api/admin/oauth/clients', {
    data: {
      ClientId: CLIENT_ID,
      DisplayName: `Playwright Continuation ${SUFFIX}`,
      ClientType: 'public',
      ConsentType: 'explicit',
      RequireClientSecret: false,
      RedirectUris: [REDIRECT_URI],
      Scopes: ['openid'],
      AllowedGrantTypes: ['authorization_code'],
    },
  })
  if (!res.ok()) throw new Error(`client create failed: ${res.status()} ${await res.text()}`)
}

/** Test user with a confirmed email (magic-link self-service requires it).
 * A fresh per-call suffix so multiple tests in this file don't collide. */
async function createUser(admin: APIRequestContext): Promise<{ id: string; email: string }> {
  const userName = `pw-cont-user-${uniqueSuffix()}`
  const email = `${userName}@modgud.test`
  const createRes = await admin.post('/api/user', {
    data: { Firstname: 'PW', Lastname: 'Continuation', Acronym: userName, Email: email, EmailConfirmed: true },
  })
  if (!createRes.ok()) throw new Error(`create user: ${createRes.status()} ${await createRes.text()}`)
  const created = await createRes.json()
  const passRes = await admin.put(`/api/user/${created.Id}/password`, { data: { Password: TEST_PASSWORD } })
  if (!passRes.ok()) throw new Error(`set-password: ${passRes.status()} ${await passRes.text()}`)
  return { id: created.Id as string, email }
}

function pkce(): { verifier: string; challenge: string } {
  const base64Url = (buf: Buffer) =>
    buf.toString('base64').replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_')
  const verifier = base64Url(randomBytes(32))
  return { verifier, challenge: base64Url(createHash('sha256').update(verifier, 'ascii').digest()) }
}

function authorizeUrl(state: string, challenge: string): string {
  const params = new URLSearchParams({
    response_type: 'code',
    client_id: CLIENT_ID,
    redirect_uri: REDIRECT_URI,
    scope: 'openid',
    state,
    code_challenge: challenge,
    code_challenge_method: 'S256',
  })
  return `/connect/authorize?${params.toString()}`
}

test.describe.configure({ mode: 'serial' })

test.describe('OIDC continuation — authorize survives the login SPA', () => {
  test.beforeAll(async () => {
    const admin = await adminContext()
    try {
      await createExplicitClient(admin)
    } finally {
      await admin.dispose()
    }
  })

  test('challenge → detour links forward the continuation → password login resumes authorize → deny returns error=access_denied to the client', async ({ page }, testInfo) => {
    const { challenge } = pkce()
    const state = randomBytes(16).toString('hex')

    // ── §1 An unauthenticated authorize is challenged to /login?redirect= ──
    await page.goto(authorizeUrl(state, challenge))
    await page.waitForURL(/\/login\?redirect=/, { timeout: 10_000 })
    await page.screenshot({ path: testInfo.outputPath('01-challenged-login.png'), fullPage: true })

    // ── §2 Mid-flow detours must forward ?redirect= (href-level, language-
    //       independent). vue-router leaves RFC 3986-legal '/' and '?'
    //       unencoded in query values, so match both encodings. ──
    const continuationHref = /redirect=(%2F|\/)connect(%2F|\/)authorize/
    const forgotLink = page.locator('a[href^="/forgot-password"]')
    await expect(forgotLink).toHaveAttribute('href', continuationHref)
    await forgotLink.click()
    await page.waitForURL(/\/forgot-password\?redirect=/, { timeout: 10_000 })

    const backLink = page.locator('a[href^="/login"]').first()
    await expect(backLink).toHaveAttribute('href', continuationHref)
    await backLink.click()
    await page.waitForURL(/\/login\?redirect=/, { timeout: 10_000 })

    // ── §3 Password login resumes the authorize flow → consent ticket ──
    await page.getByRole('textbox', { name: /benutzername|username/i }).fill(ADMIN_USER)
    await page.getByRole('textbox', { name: /passwort|password/i }).fill(ADMIN_PASSWORD)
    await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()

    // A fresh admin may hit the secure-setup (2FA nudge) interstitial first.
    // Postponing finishes through the same finishLogin(), so the continuation
    // must survive that path too.
    const postpone = page.getByRole('button', { name: /Später|Postpone|Later|Skip/i }).first()
    await Promise.race([
      page.waitForURL(/\/consent\?ticket=/, { timeout: 15_000 }),
      postpone.waitFor({ timeout: 15_000 }),
    ])
    if (!/\/consent\?ticket=/.test(page.url())) {
      await postpone.click()
    }
    await page.waitForURL(/\/consent\?ticket=/, { timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('02-consent.png'), fullPage: true })

    // ── §4 Deny → RFC 6749 §4.1.2.1 error redirect BACK to the client ──
    const redirectReq = page.waitForRequest(
      (req) => req.url().startsWith(REDIRECT_URI), { timeout: 10_000 })
    await page.getByRole('button', { name: /^Deny$|^Ablehnen$/i }).click()
    const callback = new URL((await redirectReq).url())
    expect(callback.searchParams.get('error')).toBe('access_denied')
    expect(callback.searchParams.get('state')).toBe(state)
  })

  test('magic-link login preserves the authorize continuation through the e-mail round trip', async ({ page }, testInfo) => {
    const admin = await adminContext()
    let user: { id: string; email: string }
    try {
      user = await createUser(admin)
    } finally {
      await admin.dispose()
    }

    const { challenge } = pkce()
    const state = randomBytes(16).toString('hex')

    await clearMailpit()
    const before = new Date(Date.now() - 60_000)

    // ── §1 Enter like a client app: authorize → challenged to /login?redirect= ──
    await page.goto(authorizeUrl(state, challenge))
    await page.waitForURL(/\/login\?redirect=/, { timeout: 10_000 })

    // ── §2 Request the magic link from THIS login page — the pending
    //       continuation must ride into the request ──
    await page.getByRole('button', { name: /login link via email|anmelde-link per e-mail/i }).click()
    await page.getByPlaceholder(/email@/i).fill(user.email)
    await page.getByRole('button', { name: /send link|link senden/i }).click()
    await expect(page.getByText(/login link was sent|anmelde-link.*gesendet|check your inbox|posteingang/i))
      .toBeVisible({ timeout: 10_000 })

    // ── §3 The emailed URL carries the continuation as ?redirect= ──
    const mail = await waitForMail(user.email, before, 30_000)
    const href = mail.HTML.match(/href="([^"]*magic-login[^"]*)"/)?.[1]
    expect(href, 'magic-login link present in the mail body').toBeTruthy()
    const linkUrl = new URL(href!.replace(/&amp;/g, '&'))
    expect(linkUrl.searchParams.get('redirect'), 'emailed link carries the authorize continuation')
      .toContain('/connect/authorize')

    // ── §4 Opening the link signs in AND resumes the authorize flow ──
    await page.goto(`${linkUrl.pathname}${linkUrl.search}`)
    await page.waitForURL(/\/consent\?ticket=/, { timeout: 15_000 })
    await page.screenshot({ path: testInfo.outputPath('01-consent-after-magic.png'), fullPage: true })

    // ── §5 Allow → the client receives its ?code= callback ──
    const redirectReq = page.waitForRequest(
      (req) => req.url().startsWith(REDIRECT_URI), { timeout: 10_000 })
    await page.getByRole('button', { name: /^Allow$|^Erlauben$/i }).click()
    const callback = new URL((await redirectReq).url())
    expect(callback.searchParams.get('code'), 'authorization code delivered to the client').toBeTruthy()
    expect(callback.searchParams.get('state')).toBe(state)
  })

  test('password-reset threads the authorize continuation through the e-mail round trip', async ({ page }, testInfo) => {
    const admin = await adminContext()
    let user: { id: string; email: string }
    try {
      user = await createUser(admin)
    } finally {
      await admin.dispose()
    }

    const { challenge } = pkce()
    const state = randomBytes(16).toString('hex')
    const authorize = authorizeUrl(state, challenge)

    await clearMailpit()
    const before = new Date(Date.now() - 60_000)

    // ── §1 Reach the forgot-password page carrying the pending continuation.
    //       "Forgot password?" on /login already forwards ?redirect=, so a
    //       real user arrives here with it in the URL. ──
    await page.goto(`/forgot-password?redirect=${encodeURIComponent(authorize)}`)
    await page.getByRole('textbox').first().fill(user.email)
    await page.getByRole('button', { name: /send link|link senden/i }).click()
    await expect(page.getByText(/reset link has been sent|link.*gesendet|e-?mail.*(sent|gesendet)/i))
      .toBeVisible({ timeout: 10_000 })
    await page.screenshot({ path: testInfo.outputPath('01-reset-requested.png'), fullPage: true })

    // ── §2 The emailed reset link carries the continuation as ?redirect=. ──
    const mail = await waitForMail(user.email, before, 30_000)
    const href = mail.HTML.match(/href="([^"]*reset-password[^"]*)"/)?.[1]
    expect(href, 'reset-password link present in the mail body').toBeTruthy()
    const linkUrl = new URL(href!.replace(/&amp;/g, '&'))
    expect(linkUrl.searchParams.get('redirect'), 'emailed reset link carries the authorize continuation')
      .toContain('/connect/authorize')

    // ── §3 The reset page forwards the continuation to /login on success, so
    //       a completed reset resumes the client flow rather than stranding.
    //       Copy-independent selectors (input[type=password] / submit) so the
    //       assertion doesn't hinge on the active UI language. ──
    await page.goto(`${linkUrl.pathname}${linkUrl.search}`)
    const pwInputs = page.locator('input[type="password"]')
    await pwInputs.nth(0).fill('ResetPass1234!')
    await pwInputs.nth(1).fill('ResetPass1234!')
    await page.locator('button[type="submit"]').click()
    // Success card replaces the form with a single "go to login" button.
    const toLogin = page.getByRole('button', { name: /login|anmeld/i })
    await expect(toLogin).toBeVisible({ timeout: 10_000 })
    await toLogin.click()
    await page.waitForURL(/\/login\?redirect=/, { timeout: 10_000 })
    expect(decodeURIComponent(new URL(page.url()).searchParams.get('redirect') ?? ''))
      .toContain('/connect/authorize')
  })
})
