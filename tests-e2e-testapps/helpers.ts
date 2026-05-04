import type { Page, APIRequestContext } from '@playwright/test'

export const AUTHORITY = process.env.TESTAPPS_AUTHORITY ?? 'http://localhost:9099'
export const RESOURCEAPI_URL = process.env.TESTAPPS_RESOURCEAPI_URL ?? 'http://localhost:7081'
export const BFF_URL = process.env.TESTAPPS_BFF_URL ?? 'http://localhost:7080'

export const DEMO_USER = process.env.TESTAPPS_DEMO_USER ?? 'demo.admin'
export const DEMO_PASSWORD = process.env.TESTAPPS_DEMO_PASSWORD ?? 'Demo1234!'

/**
 * Fetches a client_credentials token directly from the IdP. Mirrors what
 * Cocoar.Auth.TestApps.ConfidentialClient does — kept as a helper so other
 * specs can grab a service-account token when they need to call the
 * resource API without going through the BFF cookie session.
 */
export async function getClientCredentialsToken(
  request: APIRequestContext,
  scope = 'demo.read demo.write',
  clientId = 'demo-backend',
  clientSecret = 'demo-backend-secret-please-rotate',
): Promise<string> {
  const res = await request.post(`${AUTHORITY}/connect/token`, {
    form: {
      grant_type: 'client_credentials',
      client_id: clientId,
      client_secret: clientSecret,
      scope,
    },
  })
  if (!res.ok()) {
    throw new Error(`Token request failed: ${res.status()} ${await res.text()}`)
  }
  const body = await res.json()
  return body.access_token as string
}

/**
 * Drives the auth-server login form. The IdP renders a Vue SPA at /login
 * with German+English labels — match either. After submit the browser
 * lands back on the OIDC redirect_uri (the BFF's /signin-oidc), which
 * sets the session cookie and forwards to the original returnUrl.
 */
export async function loginViaUi(page: Page, user: string, password: string) {
  // Already on the IdP login page — fill and submit.
  await page.getByRole('textbox', { name: /benutzername|username/i }).fill(user)
  await page.getByRole('textbox', { name: /passwort|password/i }).fill(password)
  await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()
}

/**
 * Convenience: from the BFF root, click "login", run through the IdP UI,
 * and return when /bff/user becomes 200. Tests that need the post-login
 * state can call this in beforeEach.
 */
export async function bffLogin(page: Page, user = DEMO_USER, password = DEMO_PASSWORD) {
  await page.goto('/')
  // Top-level navigation — fetch isn't enough for OIDC.
  await page.goto(`/bff/login?returnUrl=${encodeURIComponent('/')}`)
  await page.waitForURL(/\/login(\?|$)/)
  await loginViaUi(page, user, password)
  await page.waitForURL(BFF_URL + '/', { timeout: 15_000 })
}
