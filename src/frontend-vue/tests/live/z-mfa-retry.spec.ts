import { test, expect, type BrowserContext, type Page } from '@playwright/test'
import { generateSync } from 'otplib'
import { readFileSync, statSync } from 'node:fs'
import { resolve, sep, extname } from 'node:path'

const fixturePath = process.env.MODGUD_LIVE_FIXTURE
const buildDir = process.env.MODGUD_BROWSER_DIST
if (!fixturePath || !buildDir) throw new Error('Run through the isolated LiveFixBrowserTests harness.')
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8'))
const origin = new URL(fixture.BaseUrl).origin
if (new URL(origin).hostname !== '127.0.0.1' || new URL(origin).protocol !== 'http:')
  throw new Error('Only the test-owned loopback Kestrel host is allowed.')
const dist = resolve(buildDir)
const index = readFileSync(resolve(dist, 'index.html'))
const mime: Record<string, string> = {
  '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css',
  '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2',
}

async function prepare(context: BrowserContext) {
  await context.grantPermissions(['local-network-access'], { origin })
  await context.addInitScript(() => localStorage.setItem('language', 'en'))
  await context.route('**/*', async route => {
    const url = new URL(route.request().url())
    if (url.origin !== origin) { await route.abort(); return }
    // No auth response, status, cookie, store or SignalR payload is substituted.
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/signalr/')) {
      await route.continue(); return
    }
    const file = resolve(dist, `.${decodeURIComponent(url.pathname)}`)
    if (file.startsWith(dist + sep)) {
      try {
        if (statSync(file).isFile()) {
          await route.fulfill({ body: readFileSync(file), contentType: mime[extname(file)] ?? 'application/octet-stream' })
          return
        }
      } catch { /* SPA history fallback */ }
    }
    if (route.request().isNavigationRequest()) {
      await route.fulfill({ body: index, contentType: 'text/html' }); return
    }
    await route.abort()
  })
}

async function fillCode(page: Page, code: string) {
  const cells = page.locator('.coar-otp-input__cell')
  await expect(cells).toHaveCount(6)
  try {
    for (let i = 0; i < 6; i++) await cells.nth(i).fill(code[i]!)
  } catch {
    // A failed fill's Playwright call log must not print the proof value.
    throw new Error('Could not enter the synthetic authenticator proof.')
  }
}

async function credentials(page: Page) {
  try {
    await page.getByPlaceholder('Username', { exact: true }).fill(fixture.Creator.UserName)
    await page.getByPlaceholder('Password', { exact: true }).fill(fixture.Creator.Password)
  } catch {
    throw new Error('Could not enter the synthetic login credentials.')
  }
  const response = page.waitForResponse(r =>
    new URL(r.url()).pathname === '/api/account/login' && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  const result = await response
  expect(result.status()).toBe(200)
  const body = await result.json()
  expect(body.RequiresMfa).toBe(true)
  expect(body.MfaMethods.includes('totp')).toBe(true)
  // The real password response can offer email fallback as well as TOTP.
  // Follow the normal choice screen instead of changing the server policy.
  if (body.MfaMethods.length > 1)
    await page.getByRole('button', { name: 'Authenticator App', exact: true }).click()
}

function wrongCode(secret: string): string {
  // Do not assume a fixed numeric value is invalid. Exclude every token in a
  // generous +/- five-minute band around this local host's current test time.
  const epoch = Math.floor(Date.now() / 1000)
  const valid = new Set(Array.from({ length: 21 }, (_, i) =>
    generateSync({ secret, epoch: epoch + (i - 10) * 30 })))
  for (let n = 0; n <= valid.size; n++) {
    const candidate = String(n).padStart(6, '0')
    if (!valid.has(candidate)) return candidate
  }
  throw new Error('Could not choose an invalid synthetic proof.')
}

let setupContext: BrowserContext
let secret: string
test.beforeAll(async ({ browser }) => {
  const admin = await browser.newContext()
  try {
    expect((await admin.request.post(`${origin}/api/account/login`, { data: fixture.Admin })).status()).toBe(200)
    expect((await admin.request.patch(`${origin}/api/admin/realm-settings`, {
      data: { SignIn: { MinimumLevel: 'Single', AdministrationMinimumLevel: 'Single' } },
    })).status()).toBe(200)
  } finally { await admin.close() }
  setupContext = await browser.newContext()
  expect((await setupContext.request.post(`${origin}/api/account/login`, { data: fixture.Creator })).status()).toBe(200)
  expect((await setupContext.request.get(`${origin}/api/account/me`)).status()).toBe(200)
  const setup = await setupContext.request.post(`${origin}/api/account/mfa/setup`)
  expect(setup.status()).toBe(200)
  // The generated test-only key lives in memory, never in a fixture or output.
  secret = (await setup.json()).SharedKey.replace(/\s/g, '')
  expect(typeof secret === 'string' && secret.length > 0).toBe(true)
  expect((await setupContext.request.post(`${origin}/api/account/mfa/verify`, {
    data: { Code: generateSync({ secret }) },
  })).status()).toBe(200)
})

test.afterAll(async () => {
  if (setupContext) {
    try {
      // Restore only this test user's factor so other harness cases do not
      // acquire a hidden ordering dependency on this file's enrollment.
      if (secret) expect((await setupContext.request.post(`${origin}/api/account/mfa/disable`)).status()).toBe(200)
    } finally { await setupContext.close() }
  }
  secret = ''
})

test('real password/TOTP login: wrong code stays local, valid retry reaches profile', async ({ browser }) => {
  const context = await browser.newContext()
  try {
    await prepare(context)
    const page = await context.newPage()
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.name))
    const path = '/login?redirect=%2Fprofile'
    await page.goto(origin + path)
    await credentials(page)
    expect((await context.request.get(`${origin}/api/account/me`)).status()).toBe(401)
    await fillCode(page, wrongCode(secret))
    const refused = page.waitForResponse(r => new URL(r.url()).pathname === '/api/account/mfa/login')
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    expect((await refused).status()).toBe(401)
    await expect(page.getByText('Invalid code. Please try again.', { exact: true })).toBeVisible()
    await expect(page).toHaveURL(origin + path)
    expect((await context.request.get(`${origin}/api/account/me`)).status()).toBe(401)
    await fillCode(page, generateSync({ secret }))
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    await expect(page).toHaveURL(origin + '/profile')
    expect((await context.request.get(`${origin}/api/account/me`)).status()).toBe(200)
    expect(errors).toEqual([])
  } finally { await context.close() }
})

test('real partial TOTP retry retains MFA, step-up, reauth and admin continuation flags', async ({ browser }) => {
  const context = await browser.newContext()
  try {
    await prepare(context)
    const login = await context.request.post(`${origin}/api/account/login`, { data: fixture.Creator })
    expect(login.status()).toBe(200)
    expect((await login.json()).RequiresMfa).toBe(true)
    const page = await context.newPage()
    const path = '/login?mfa=totp&stepup=1&reauth=1&for=admin&redirect=%2Fadmin%2Fservice-accounts'
    await page.goto(origin + path)
    await fillCode(page, wrongCode(secret))
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    await expect(page.getByText('Invalid code. Please try again.', { exact: true })).toBeVisible()
    await expect(page).toHaveURL(origin + path)
    expect((await context.request.get(`${origin}/api/account/me`)).status()).toBe(401)
    await fillCode(page, generateSync({ secret }))
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    await expect(page).toHaveURL(origin + '/admin/service-accounts')
    expect((await context.request.get(`${origin}/api/service-account`)).status()).toBe(200)
  } finally { await context.close() }
})

test('missing partial cookie cannot authenticate; explicit credential restart retains profile destination', async ({ browser }) => {
  const context = await browser.newContext()
  try {
    await prepare(context)
    const login = await context.request.post(`${origin}/api/account/login`, { data: fixture.Creator })
    expect((await login.json()).RequiresMfa).toBe(true)
    // This models the server-visible absence after partial-cookie expiry/loss,
    // not literal clock advancement or a weaker server expiry configuration.
    await context.clearCookies()
    const page = await context.newPage()
    const path = '/login?mfa=totp&redirect=%2Fprofile'
    await page.goto(origin + path)
    await fillCode(page, generateSync({ secret }))
    const refused = page.waitForResponse(r => new URL(r.url()).pathname === '/api/account/mfa/login')
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    expect((await refused).status()).toBe(401)
    await expect(page.getByText('Invalid code. Please try again.', { exact: true })).toBeVisible()
    await expect(page).toHaveURL(origin + path)
    expect((await context.request.get(`${origin}/api/account/me`)).status()).toBe(401)
    await page.getByRole('button', { name: 'Back to login', exact: true }).click()
    await credentials(page)
    await fillCode(page, generateSync({ secret }))
    await page.getByRole('button', { name: 'Confirm', exact: true }).click()
    await expect(page).toHaveURL(origin + '/profile')
    expect((await context.request.get(`${origin}/api/account/me`)).status()).toBe(200)
  } finally { await context.close() }
})
