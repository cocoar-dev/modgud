import { test, expect, request as pwRequest } from '@playwright/test'
import { apiLogin, uniqueSuffix } from './helpers'
import { createHash, randomBytes } from 'node:crypto'

/**
 * Dynamic Client Registration end-to-end through the SPA. Three things
 * this spec adds on top of the xUnit DcrFullFlowTests:
 *
 *  1. The realm-admin UI flow that enables DCR (toggle in
 *     /admin/realm-settings → DCR tab).
 *  2. The OAuth Clients grid actually showing a DCR-issued client
 *     (column + filter + Registration Info tab).
 *  3. The crucial user-facing safety primitive — the consent screen
 *     renders the `[unverified]` marker + warning callout for a DCR
 *     client. This is the bit that xUnit cannot prove; it's pure
 *     Vue rendering on top of the consent-info DTO.
 */

const ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'

// Unique-per-run names so re-runs against the same DB don't collide.
const SUFFIX = uniqueSuffix()
const API_NAME = `https://dcr-pw-${SUFFIX}.modgud.test/`
const SCOPE_NAME = `dcr-pw-scope-${SUFFIX}`
const CLIENT_NAME = `Playwright DCR ${SUFFIX}`
// Loopback redirect — the browser will navigate to it after consent,
// nothing is listening, we capture the URL on the `commit` event.
const REDIRECT_URI = 'http://localhost/dcr-pw-cb'

test.describe.configure({ mode: 'serial' })

test.beforeEach(async ({ page }) => {
  await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
})

test.describe('DCR — end-to-end', () => {
  test('admin enables DCR via UI, agent registers, consent shows [unverified] marker', async ({ page, baseURL }) => {
    // ── §1: enable DCR via the realm-settings UI ─────────────────────
    await page.goto('/admin/realm-settings')
    // The DCR settings live behind a <CoarTab>, not a button. Click it by text
    // scoped to the tab bar so the selector is robust to the tab's ARIA role.
    await page.locator('.tab-bar').getByText(/Dynamic Client Registration/i).click()
    const dcrEnable = page.getByRole('checkbox',
      { name: /Enable Dynamic Client Registration/i })
    // The real <input> is visually replaced by its CoarCheckbox label, which
    // intercepts pointer events — click the label (the human path) to toggle.
    if (!(await dcrEnable.isChecked())) {
      await page.getByText(/Enable Dynamic Client Registration/i).click()
    }
    await expect(dcrEnable).toBeChecked()

    // Generous: production rate-limits would trip mid-suite once
    // re-runs accumulate. The fields are plain numeric inputs.
    await page
      .getByLabel(/Rate limit per source IP/i)
      .fill('10000')
    await page
      .getByLabel(/Rate limit per realm/i)
      .fill('10000')
    await page.getByRole('button', { name: /^Save$|^Speichern$/i }).click()
    await expect(page.getByText(/Saved\.|Gespeichert/i)).toBeVisible({ timeout: 5_000 })

    // ── §2: seed an OAuthApi with AllowDCR=true (API, not UI) ────────
    // The OAuth-Apis admin UI is covered by 10-admin.spec.ts; here we
    // just create the resource so the DCR client has something legal
    // to ask for.
    const apiCreate = await page.request.post('/api/admin/oauth/apis', {
      data: {
        Name: API_NAME,
        DisplayName: API_NAME,
        Enabled: true,
        AllowDynamicRegistration: true,
      },
    })
    expect(apiCreate.ok()).toBeTruthy()

    // ── §3: seed an OAuthScope with AllowDCRClients=true ─────────────
    const scopeCreate = await page.request.post('/api/admin/oauth/scopes', {
      data: {
        Name: SCOPE_NAME,
        DisplayName: SCOPE_NAME,
        Resources: [API_NAME],
        Enabled: true,
        AllowDynamicRegistrationClients: true,
      },
    })
    expect(scopeCreate.ok()).toBeTruthy()

    // ── §4: anonymous DCR registration (no UI — it's HTTP) ───────────
    // Use a fresh request context (no auth cookie) so the endpoint sees
    // an anonymous caller, the way an agent actually hits it.
    const anonContext = await pwRequest.newContext({ baseURL })
    const regResp = await anonContext.post(`/connect/register`, {
      data: {
        client_name: CLIENT_NAME,
        redirect_uris: [REDIRECT_URI],
        grant_types: ['authorization_code'],
        scope: `openid ${SCOPE_NAME}`,
      },
    })
    expect(regResp.status()).toBe(201)
    const regBody = await regResp.json()
    const clientId: string = regBody.client_id
    expect(clientId).toMatch(/^dcr-/)
    await anonContext.dispose()

    // ── §5: admin grid surfaces the DCR client ───────────────────────
    await page.goto('/admin/oauth/clients')
    // The DCR-only filter should hide all admin-created clients and
    // leave just our new one visible.
    // Same CoarCheckbox label-intercept caveat as the DCR-enable toggle above.
    const dcrOnly = page.getByRole('checkbox', { name: /DCR only/i })
    if (!(await dcrOnly.isChecked())) await page.getByText(/DCR only/i).click()
    await expect(dcrOnly).toBeChecked()
    await expect(page.getByRole('gridcell', { name: clientId })).toBeVisible({ timeout: 10_000 })

    // ── §6: drive the OAuth dance through the consent screen ─────────
    const verifier = generatePkceVerifier()
    const challenge = generatePkceS256Challenge(verifier)
    const state = randomBytes(16).toString('hex')

    const authorizeParams = new URLSearchParams({
      response_type: 'code',
      client_id: clientId,
      redirect_uri: REDIRECT_URI,
      scope: `openid ${SCOPE_NAME}`,
      state,
      code_challenge: challenge,
      code_challenge_method: 'S256',
      resource: API_NAME,
    })
    await page.goto(`/connect/authorize?${authorizeParams.toString()}`)

    // The authorize endpoint redirects to /consent?ticket=… for explicit-
    // consent clients (DCR is always explicit). We should land on the
    // consent SPA route.
    await page.waitForURL(/\/consent\?ticket=/, { timeout: 10_000 })

    // ── §7: THE [unverified] marker — pure UI assertion ──────────────
    await expect(page.locator('.unverified-tag')).toBeVisible()
    await expect(page.locator('.unverified-tag')).toContainText(/unverified/i)
    await expect(page.getByText(/registered itself|hat sich selbst registriert/i)).toBeVisible()

    // ── §8: click Allow, capture code from redirect ──────────────────
    // The redirect URI is a dead loopback (nothing listening), so the
    // navigation never commits — wait for the request to be *issued*
    // instead, which fires before the connection is refused.
    const redirectReq = page.waitForRequest(
      req => req.url().startsWith(REDIRECT_URI), { timeout: 10_000 })
    await page.getByRole('button', { name: /^Erlauben$|^Allow$/i }).click()
    const code = new URL((await redirectReq).url()).searchParams.get('code')
    expect(code, 'Expected ?code= in the redirect request URL').toBeTruthy()

    // ── §9: token exchange — anonymous (public PKCE client) ──────────
    const tokenContext = await pwRequest.newContext({ baseURL })
    const tokenResp = await tokenContext.post('/connect/token', {
      form: {
        grant_type: 'authorization_code',
        code: code!,
        client_id: clientId,
        redirect_uri: REDIRECT_URI,
        code_verifier: verifier,
        resource: API_NAME,
      },
    })
    expect(tokenResp.ok()).toBeTruthy()
    const tokenBody = await tokenResp.json()
    expect(tokenBody.access_token).toBeTruthy()
    expect(tokenBody.token_type).toMatch(/bearer/i)
    await tokenContext.dispose()
  })
})

// ─── PKCE + helpers ────────────────────────────────────────────────────

function generatePkceVerifier(): string {
  return base64Url(randomBytes(32))
}

function generatePkceS256Challenge(verifier: string): string {
  return base64Url(createHash('sha256').update(verifier, 'ascii').digest())
}

function base64Url(buf: Buffer): string {
  return buf.toString('base64')
    .replace(/=/g, '')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
}
