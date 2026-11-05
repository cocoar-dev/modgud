import { test, expect, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { resolve, sep, extname } from 'node:path'

// Real SPA, login view, Pinia and HTTP client. Only HTTP/SignalR are simulated.
const origin = 'http://modgud.invalid'
const buildDir = process.env.MODGUD_BROWSER_DIST
if (!buildDir) throw new Error('Set MODGUD_BROWSER_DIST to an isolated build.')
const dist = resolve(buildDir)
const index = readFileSync(resolve(dist, 'index.html'))
const mime: Record<string, string> = {
  '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css',
  '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2',
}
const otp = '.coar-otp-input__cell'

async function fillCode(page: Page, code: string) {
  const cells = page.locator(otp)
  await expect(cells).toHaveCount(6)
  for (let i = 0; i < 6; i++) await cells.nth(i).fill(code[i]!)
}

async function fixture(page: Page, method: 'totp' | 'email', options: {
  expired?: boolean; requestFailure?: boolean; failures?: number[]
} = {}) {
  let partial = !options.expired
  let authenticated = false
  let attempts = 0
  let requests = 0
  let credentials = 0
  const unexpected: string[] = []
  const errors: string[] = []
  const continuations: string[] = []
  const post = method === 'totp' ? '/api/account/mfa/login' : '/api/account/email-otp/login'
  await page.addInitScript(() => localStorage.setItem('language', 'en'))
  page.on('pageerror', error => errors.push(error.name))
  await page.context().routeWebSocket('**/*', socket => socket.close())
  await page.context().route('**/*', async route => {
    const request = route.request()
    const url = new URL(request.url())
    if (url.origin !== origin) { unexpected.push('external request'); await route.abort(); return }
    const path = url.pathname
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body })
    if (path.startsWith('/signalr/')) { await json({}, 403); return }
    if (path === '/api/app-info') {
      await json({
        SignIn: { Password: true, Passkey: false, EmailCode: false, MinimumLevel: 'Single' },
        Features: { PageBuilder: false, PositionTerminals: false }, MagicLinkSelfService: false,
      }); return
    }
    if (path === '/api/account/external-logins') { await json([]); return }
    if (path === '/api/account/self-registration-info') { await json({ Enabled: false }); return }
    if (path === '/api/account/me') {
      await json(authenticated ? {
        Id: 'mfa-user', UserName: 'mfa-user', Email: 'mfa@example.test',
        Permissions: ['service-account:read'], EmailConfirmed: true,
        Has2FA: true, TwoFactorMethods: [method], AdministrationNeedsStepUp: false,
      } : {}, authenticated ? 200 : 401); return
    }
    if (path === '/api/account/login' && request.method() === 'POST') {
      credentials++; partial = true
      await json({ RequiresMfa: true, MfaMethods: [method] }); return
    }
    if (path === '/api/account/step-up') {
      await json({ RequiresMfa: true, MfaMethods: [method] }); return
    }
    if (path === '/api/account/email-otp/login/request') {
      requests++
      const failure = options.requestFailure && requests === 1
      await json({ Message: failure ? 'Invalid credentials' : 'Verification code sent' }, failure ? 401 : 200)
      return
    }
    if (path === post && request.method() === 'POST') {
      const failure = !partial ? 401 : (options.failures ?? [401])[attempts] ?? 200
      attempts++
      if (failure === 200) authenticated = true
      await json(failure === 200 ? { Message: 'Login successful' } : { detail: 'Invalid code. Please try again.' }, failure)
      return
    }
    if (path === '/api/auth/deletion-status') { await json({ IsPending: false }); return }
    if (path === '/api/account/profile/request') { await json({ Open: null, LastTerminal: null }); return }
    if (['/api/inbox', '/api/account/passkey', '/api/account/external-links', '/api/service-account'].includes(path)) {
      await json([]); return
    }
    if (path === '/api/account/mfa/status') { await json({ Enabled: true }); return }
    if (path === '/api/account/email-otp/status') { await json({ Enabled: method === 'email', HasEmail: true }); return }
    if (path === '/connect/authorize') {
      continuations.push(url.pathname + url.search)
      await route.fulfill({ contentType: 'text/html', body: '<p>Continuation reached</p>' }); return
    }
    if (path.startsWith('/api/')) { unexpected.push(`${request.method()} ${path}`); await json({}, 403); return }
    const file = resolve(dist, `.${decodeURIComponent(path)}`)
    if (file.startsWith(dist + sep)) {
      try {
        // One read, no stat first: it throws for a missing file or a directory.
        const body = readFileSync(file)
        await route.fulfill({ body, contentType: mime[extname(file)] ?? 'application/octet-stream' })
        return
      } catch { /* SPA history fallback */ }
    }
    if (request.isNavigationRequest()) { await route.fulfill({ body: index, contentType: 'text/html' }); return }
    unexpected.push(path); await route.abort()
  })
  return { attempts: () => attempts, requests: () => requests, credentials: () => credentials, continuations, unexpected, errors }
}

const targets = [
  '/connect/authorize?client_id=synthetic&scope=openid&state=retry',
  '/profile',
  '/admin/service-accounts',
]
for (const target of targets) {
  test(`TOTP401 stays local and a successful retry preserves ${target.split('?')[0]}`, async ({ page }) => {
    const f = await fixture(page, 'totp')
    const path = `/login?mfa=totp&stepup=1&reauth=1&for=admin&redirect=${encodeURIComponent(target)}`
    await page.goto(origin + path)
    await fillCode(page, '123456')
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    await expect(page.getByText('Invalid code. Please try again.', { exact: true })).toBeVisible()
    await expect(page).toHaveURL(origin + path)
    expect(f.attempts()).toBe(1)
    expect(await page.evaluate(async () => (await fetch('/api/account/me')).status)).toBe(401)
    await fillCode(page, '654321')
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    await expect(page).toHaveURL(origin + target)
    expect(f.attempts()).toBe(2)
    expect(f.continuations).toEqual(target.startsWith('/connect/') ? [target] : [])
    expect(f.unexpected).toEqual([])
    expect(f.errors).toEqual([])
  })
}

test('email MFA400/401 remain local; only a later server-accepted submission completes', async ({ page }) => {
  const f = await fixture(page, 'email', { failures: [400, 401] })
  const path = '/login?mfa=email&redirect=%2Fprofile'
  await page.goto(origin + path)
  for (let attempt = 1; attempt <= 2; attempt++) {
    await fillCode(page, '123456')
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    await expect.poll(f.attempts).toBe(attempt)
    await expect(page.getByText('Invalid code. Please try again.', { exact: true })).toBeVisible()
    await expect(page).toHaveURL(origin + path)
  }
  await fillCode(page, '654321')
  await page.getByRole('button', { name: 'Confirm', exact: true }).click()
  await expect(page).toHaveURL(origin + '/profile')
  expect(f.requests()).toBe(1)
  expect(f.unexpected).toEqual([])
  expect(f.errors).toEqual([])
})

test('email challenge request401 keeps the continuation and exposes an explicit resend', async ({ page }) => {
  const f = await fixture(page, 'email', { requestFailure: true, failures: [] })
  const path = '/login?mfa=email&redirect=%2Fprofile'
  await page.goto(origin + path)
  await expect.poll(f.requests).toBe(1)
  await expect(page.getByText('Error sending code.', { exact: true })).toBeVisible()
  await expect(page).toHaveURL(origin + path)
  await page.getByRole('button', { name: 'Resend code', exact: true }).click()
  await expect.poll(f.requests).toBe(2)
  await fillCode(page, '654321')
  await page.getByRole('button', { name: 'Confirm', exact: true }).click()
  await expect(page).toHaveURL(origin + '/profile')
  expect(f.unexpected).toEqual([])
  expect(f.errors).toEqual([])
})

test('expired partial login denies MFA; back to credentials and a fresh login preserve the destination', async ({ page }) => {
  const f = await fixture(page, 'totp', { expired: true, failures: [] })
  const path = '/login?mfa=totp&redirect=%2Fprofile'
  await page.goto(origin + path)
  await fillCode(page, '123456')
  await page.getByRole('button', { name: 'Confirm', exact: true }).click()
  await expect(page.getByText('Invalid code. Please try again.', { exact: true })).toBeVisible()
  await expect(page).toHaveURL(origin + path)
  expect(await page.evaluate(async () => (await fetch('/api/account/me')).status)).toBe(401)
  await page.getByRole('button', { name: 'Back to login', exact: true }).click()
  await page.getByPlaceholder('Username', { exact: true }).fill('mfa-user')
  await page.getByPlaceholder('Password', { exact: true }).fill('synthetic-password')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await fillCode(page, '654321')
  await page.getByRole('button', { name: 'Confirm', exact: true }).click()
  await expect(page).toHaveURL(origin + '/profile')
  expect(f.credentials()).toBe(1)
  expect(f.attempts()).toBe(2)
  expect(f.unexpected).toEqual([])
  expect(f.errors).toEqual([])
})
