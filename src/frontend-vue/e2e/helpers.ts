import { type Page, type APIRequestContext, expect } from '@playwright/test'

/**
 * Login via the UI — fills username + password and submits.
 */
export async function login(page: Page, userName: string, password: string) {
  await page.goto('/login')
  await page.getByRole('textbox', { name: /benutzername|username/i }).fill(userName)
  await page.getByRole('textbox', { name: /passwort|password/i }).fill(password)
  await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()
  await page.waitForURL((url) => !url.pathname.includes('/login'), { timeout: 15_000 })
}

/**
 * Login via fetch inside the page context — ensures cookies are set correctly.
 */
export async function apiLoginAndNavigate(page: Page, userName: string, password: string, targetUrl = '/dashboard') {
  await page.goto('/login')
  const result = await page.evaluate(async ({ userName, password }) => {
    const res = await fetch('/api/account/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ UserName: userName, Password: password }),
    })
    return { ok: res.ok, status: res.status }
  }, { userName, password })

  if (!result.ok) throw new Error(`Login failed: ${result.status}`)
  await page.goto(targetUrl)
}

/**
 * Login as test admin (ka) via API.
 */
export async function apiLoginAsAdmin(page: Page) {
  await page.request.post('/api/account/login', {
    data: { UserName: 'ka', Password: 'Test1234!' },
  })
}

/**
 * Setup a CDP-based virtual authenticator for WebAuthn/Passkey testing.
 */
export async function addVirtualAuthenticator(page: Page) {
  const cdp = await page.context().newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  const { authenticatorId } = await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: {
      protocol: 'ctap2',
      transport: 'internal',
      hasResidentKey: true,
      hasUserVerification: true,
      isUserVerified: true,
      automaticPresenceSimulation: true,
    },
  })
  return { cdp, authenticatorId }
}

/**
 * Remove a virtual authenticator. Best-effort cleanup — callers use this in
 * finally blocks, where throwing would obscure the real test failure in the
 * previous step.
 */
export async function removeVirtualAuthenticator(cdp: any, authenticatorId: string) {
  try {
    await cdp.send('WebAuthn.removeVirtualAuthenticator', { authenticatorId })
    await cdp.send('WebAuthn.disable')
  } catch { /* swallow — the underlying test failure is what matters */ }
}

// ────────────────────────────────────────────────────────────────────
// OIDC / TestIdP helpers
// ────────────────────────────────────────────────────────────────────

/**
 * Create an IdpConfig pointing at the E2E TestIdP and enable it. Returns the
 * config id + public redirect URI — spec code registers the redirect URI with
 * TestIdP and then drives the login flow.
 *
 * Requires the caller's `request` context to be authenticated as admin (the
 * config endpoints are admin-only).
 */
export async function createTestIdpConfig(request: APIRequestContext, opts?: {
  displayName?: string
  userUpdateScript?: string
  scopes?: string[]
  autoCreateUsers?: boolean
  allowLinking?: boolean
}) {
  const testIdpIssuer = process.env.E2E_TESTIDP_ISSUER
  const clientId = process.env.E2E_TESTIDP_CLIENT_ID
  const clientSecret = process.env.E2E_TESTIDP_CLIENT_SECRET
  if (!testIdpIssuer || !clientId || !clientSecret) {
    throw new Error('E2E TestIdP env vars not set — global-setup must run before specs')
  }
  const metadataUri = `${testIdpIssuer.replace(/\/$/, '')}/.well-known/openid-configuration`

  // 1. Create via admin API
  const createRes = await request.post('/api/admin/idp-config', {
    data: {
      Flavor: 'GenericOidc',  // IdpFlavor.GenericOidc — PascalCase, not kebab-case
      DisplayName: opts?.displayName ?? 'TestIdP E2E',
      FlavorData: { MetadataUri: metadataUri },
    },
  })
  if (!createRes.ok()) throw new Error(`Create IdpConfig failed: ${createRes.status()} ${await createRes.text()}`)
  const created = await createRes.json()
  const id = created.Id as string
  const redirectUri = created.RedirectUri as string

  // 2. Fill in clientId + scopes + user-update-script (create only takes Flavor/DisplayName/FlavorData).
  //    Empty UserUpdateScript here would overwrite the flavor-default that the
  //    create-command applied, breaking JIT (no email → Idp.EmailRequired). So we
  //    send a working minimal script that maps the standard OIDC claims onto the
  //    user record — sufficient for every TestIdP seed user (alice, bob, mfauser).
  const defaultUserUpdateScript = opts?.userUpdateScript ?? `
    (claims) => ({
      firstname: claims.given_name ?? claims.name?.split(' ')[0],
      lastname: claims.family_name ?? claims.name?.split(' ').slice(1).join(' '),
      email: claims.email,
      acronym: (claims.given_name?.[0] ?? claims.name?.[0] ?? '') + (claims.family_name?.[0] ?? '')
    })
  `.trim()

  const putRes = await request.put(`/api/admin/idp-config/${id}`, {
    data: {
      DisplayName: opts?.displayName ?? 'TestIdP E2E',
      ClientId: clientId,
      Scopes: opts?.scopes ?? ['openid', 'profile', 'email'],
      UserUpdateScript: defaultUserUpdateScript,
      StoreRawClaims: true,
      RawClaimsRetentionDays: 7,
      AutoCreateUsers: opts?.autoCreateUsers ?? true,
      AllowLinking: opts?.allowLinking ?? true,
      TrustForEmailLink: false,
      AllowedEmailDomains: [],
      IconName: 'key-round',
      ButtonColorHex: null,
      FlavorData: { MetadataUri: metadataUri },
    },
  })
  if (!putRes.ok()) throw new Error(`Update IdpConfig failed: ${putRes.status()} ${await putRes.text()}`)

  // 3. Set client secret (separate endpoint — only accepts plaintext).
  const secretRes = await request.post(`/api/admin/idp-config/${id}/secret`, {
    data: { Secret: clientSecret },
  })
  if (!secretRes.ok()) throw new Error(`Set secret failed: ${secretRes.status()} ${await secretRes.text()}`)

  // 4. Enable.
  const enableRes = await request.post(`/api/admin/idp-config/${id}/enable`)
  if (!enableRes.ok()) throw new Error(`Enable failed: ${enableRes.status()} ${await enableRes.text()}`)

  // 5. Register the callback URI with TestIdP so OpenIddict accepts it.
  const regRes = await fetch(`${testIdpIssuer}admin/register-redirect`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ClientId: clientId, RedirectUri: redirectUri }),
  })
  if (!regRes.ok) throw new Error(`TestIdP register-redirect failed: ${regRes.status} ${await regRes.text()}`)

  return { id, redirectUri }
}

/**
 * Delete an IdpConfig by id. Safe to call multiple times.
 */
export async function deleteTestIdpConfig(request: APIRequestContext, id: string) {
  try { await request.delete(`/api/admin/idp-config/${id}`) } catch { /* ignore */ }
}
