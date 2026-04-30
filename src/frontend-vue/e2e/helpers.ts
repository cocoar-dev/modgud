import { type Page } from '@playwright/test'

/**
 * Login via the UI — fills username + password and submits, waits until the
 * auth cookie is in place. The SPA may either navigate to `/dashboard` (clean
 * login) or render the 2FA-grace-period modal in place on `/login` (with a
 * "Später" / "Skip" button). Both count as "logged in" — the modal stays
 * dismissable, so a downstream test can click "Später" if it needs the
 * dashboard.
 */
export async function login(page: Page, userName: string, password: string) {
  await page.goto('/login')
  await page.getByRole('textbox', { name: /benutzername|username/i }).fill(userName)
  await page.getByRole('textbox', { name: /passwort|password/i }).fill(password)
  await page.getByRole('button', { name: /anmelden|sign in|login/i }).first().click()
  // Either:
  //   (a) navigation to /dashboard (clean login), or
  //   (b) the secure-setup grace-period modal pops up on top of /login.
  await Promise.race([
    page.waitForURL(/\/dashboard/, { timeout: 15_000 }),
    page.getByRole('button', { name: /Später|Later|Skip/i }).first().waitFor({ timeout: 15_000 }),
  ])
}

/**
 * Login via the JSON API in the page context — bypasses the form, useful when
 * a spec needs the user authenticated but doesn't care about the UI flow. The
 * cookie lands on the page's origin, so subsequent navigation is authenticated.
 */
export async function apiLogin(page: Page, userName: string, password: string) {
  const res = await page.request.post('/api/account/login', {
    data: { UserName: userName, Password: password, RememberMe: false },
  })
  if (!res.ok()) {
    throw new Error(`Login failed for ${userName}: ${res.status()} ${await res.text()}`)
  }
}

/**
 * Programmatically register a virtual WebAuthn authenticator on the current
 * page's CDP session. Usable for passkey specs without real hardware.
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

/** Best-effort cleanup — swallows errors so a teardown failure doesn't mask the test failure. */
export async function removeVirtualAuthenticator(cdp: any, authenticatorId: string) {
  try {
    await cdp.send('WebAuthn.removeVirtualAuthenticator', { authenticatorId })
    await cdp.send('WebAuthn.disable')
  } catch { /* swallow */ }
}
