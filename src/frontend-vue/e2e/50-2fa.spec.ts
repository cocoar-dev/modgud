import { test, expect } from '@playwright/test'
import { generateSync } from 'otplib'
import { apiLogin, uniqueSuffix } from './helpers'

// otplib v13 is a TypeScript-first rewrite — `generateSync({ secret })`
// returns the current TOTP code with the same defaults ASP.NET Identity
// uses (SHA-1, 30 s window, 6 digits, RFC 6238).
function totp(secret: string): string {
  return generateSync({ secret })
}

/**
 * §3 of the manual checklist — TOTP enable + MFA login flow end-to-end
 * against the production-mode container.
 *
 * `otplib`'s `totp(secret)` is a software implementation
 * of RFC 6238 over the same SHA-1 + 30 s window ASP.NET Identity uses on
 * the server. As long as the test machine's clock is in sync with the
 * container's clock (which Docker guarantees on the same host), the
 * generated code validates without any plumbing-level shortcut. This is
 * the same trick the legacy `_legacy/auth-enforcement.spec.ts` used.
 */

const ADMIN_USER = 'admin'
const ADMIN_PASSWORD = 'ABC12abc!'
const TEST_PASSWORD = 'TestPass1234!'

const SUFFIX = uniqueSuffix()
const userName = `totp-${SUFFIX}`
// A user signs in with the e-mail address: the account's user name is the address, the
// acronym is only a display handle.
const email = `${userName}@modgud.test`

test.describe.configure({ mode: 'serial' })

test.beforeAll(async ({ request }) => {
  const adminLogin = await request.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!adminLogin.ok()) throw new Error('admin login failed')

  const created = await (await request.post('/api/user', {
    data: {
      Firstname: 'Totp', Lastname: 'User', Acronym: userName,
      Email: email,
    },
  })).json()
  const passRes = await request.put(`/api/user/${created.Id}/password`, {
    data: { Password: TEST_PASSWORD },
  })
  if (!passRes.ok()) throw new Error(`set-password: ${passRes.status()}`)
  await request.post('/api/account/logout')
})

test('§3 the setup dialog draws the QR code itself', async ({ page, baseURL }) => {
  // The QR code carries the TOTP secret, so the page has to draw it itself: an image from a
  // QR service would hand the secret to that service, and the production CSP
  // (`img-src 'self' data:`) refuses such an image anyway — a broken picture in the dialog.
  // The specs below talk to the API only; this one opens the dialog.
  await apiLogin(page, email, TEST_PASSWORD)

  const origin = new URL(baseURL!).origin
  const foreignRequests: string[] = []
  const cspViolations: string[] = []
  page.on('request', (r) => {
    const url = new URL(r.url())
    if (url.protocol.startsWith('http') && url.origin !== origin) foreignRequests.push(r.url())
  })
  page.on('console', (m) => {
    if (m.type() === 'error' && /Content Security Policy/i.test(m.text())) cspViolations.push(m.text())
  })

  await page.goto('/profile')
  await page.getByRole('menuitem', { name: /Sicherheit|Security/i }).click()
  const setupResponse = page.waitForResponse((r) => r.url().endsWith('/api/account/mfa/setup'))
  await page.getByRole('button', { name: /MFA einrichten|Set up MFA/i }).click()
  expect((await setupResponse).ok()).toBeTruthy()

  // Drawn in the page: an <svg> with the modules as one path, not an image from elsewhere.
  const qr = page.locator('svg[role="img"][aria-label="QR Code"]')
  await expect(qr).toBeVisible()
  const modules = await qr.locator('path').getAttribute('d')
  expect(modules?.length ?? 0).toBeGreaterThan(1000)

  expect(foreignRequests).toEqual([])
  expect(cspViolations).toEqual([])
})

test('§3 TOTP setup + sign-in with code', async ({ page, request }) => {
  // Authenticate the user (cookie via page.request).
  await apiLogin(page, email, TEST_PASSWORD)

  // Step 1: Request authenticator setup. Backend resets the key + returns
  // the otpauth:// URI for the QR plus the human-format `SharedKey`.
  const setupRes = await page.request.post('/api/account/mfa/setup')
  if (!setupRes.ok()) throw new Error(`setup: ${setupRes.status()} ${await setupRes.text()}`)
  const setup = await setupRes.json() as { SharedKey: string; AuthenticatorUri: string }

  // SharedKey is base32 with spaces every 4 chars for readability — strip
  // them. otplib expects the bare key.
  const secret = setup.SharedKey.replace(/\s+/g, '')
  expect(secret.length).toBeGreaterThan(0)

  // Step 2: Generate a code from the secret and post it to /verify.
  const code = totp(secret)
  const verifyRes = await page.request.post('/api/account/mfa/verify', {
    data: { Code: code },
  })
  if (!verifyRes.ok()) throw new Error(`verify: ${verifyRes.status()} ${await verifyRes.text()}`)
  const verifyBody = await verifyRes.json()
  expect(verifyBody.Enabled).toBe(true)

  // Step 3: Sign out and re-attempt login. /api/account/login should now
  // return `{ RequiresMfa: true, MfaMethods: ["totp"] }` because the user
  // has TOTP enabled but hasn't completed the second factor yet.
  await page.request.post('/api/account/logout')

  const firstLogin = await request.post('/api/account/login', {
    data: { UserName: email, Password: TEST_PASSWORD, RememberMe: false },
  })
  expect(firstLogin.ok()).toBeTruthy()
  const firstBody = await firstLogin.json()
  expect(firstBody.RequiresMfa).toBe(true)
  expect(firstBody.MfaMethods).toContain('totp')

  // Step 4: Complete sign-in with a fresh TOTP code. Identity stores a
  // partial sign-in cookie; the same `request` context already carries it
  // because the previous login put it there.
  const mfaCode = totp(secret)
  const mfaLogin = await request.post('/api/account/mfa/login', {
    data: { Code: mfaCode, RememberMe: false },
  })
  if (!mfaLogin.ok()) throw new Error(`mfa login: ${mfaLogin.status()} ${await mfaLogin.text()}`)

  // Step 5: /me on the same request context now returns the user — full
  // sign-in completed.
  const me = await (await request.get('/api/account/me')).json()
  expect(me.UserName).toBe(email)
  expect(me.Has2FA).toBe(true)
  expect(me.TwoFactorMethods).toContain('totp')
})

test('§3 invalid TOTP on second-factor login is rejected', async ({ request }) => {
  // The previous test enabled TOTP. Try a wrong code on a fresh login.
  const passRes = await request.post('/api/account/login', {
    data: { UserName: email, Password: TEST_PASSWORD, RememberMe: false },
  })
  expect(passRes.ok()).toBeTruthy()
  expect((await passRes.json()).RequiresMfa).toBe(true)

  const wrong = await request.post('/api/account/mfa/login', {
    data: { Code: '000000', RememberMe: false },
  })
  expect(wrong.status()).toBe(401)
})
