import { test, expect, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { resolve, sep, extname } from 'node:path'

// Load the real built SPA through route.fulfill, not a running server. Every
// request is intercepted; unknown requests fail closed instead of reaching a host.
const origin = 'http://modgud.invalid'
const buildDir = process.env.MODGUD_BROWSER_DIST
if (!buildDir) throw new Error('Set MODGUD_BROWSER_DIST to an existing isolated Vite build directory.')
const dist = resolve(buildDir)
const index = readFileSync(resolve(dist, 'index.html'))
const contentTypes: Record<string, string> = {
  '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css',
  '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2',
}

async function fixture(page: Page, block: string | null, reauthenticate = false) {
  const seen = new Set<string>()
  const unexpected: string[] = []
  let deletionRequests = 0
  let pending = false
  await page.addInitScript(() => localStorage.setItem('language', 'en'))
  await page.context().routeWebSocket('**/*', socket => socket.close())
  await page.context().route('**/*', async route => {
    const request = route.request()
    const url = new URL(request.url())
    if (url.origin !== origin) {
      unexpected.push(request.url())
      await route.abort()
      return
    }
    const path = url.pathname
    seen.add(path)
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body })
    if (path.startsWith('/signalr/')) {
      await json({ Message: 'No SignalR server in this isolated fixture' }, 403)
      return
    }
    if (path === '/api/app-info') {
      await json({
        SignIn: { Password: true, EmailCode: true, Passkey: false, MinimumLevel: 'Multi' },
        TwoFactorGracePeriodDays: 0,
      })
      return
    }
    if (path === '/api/account/me') {
      await json({
        Id: 'isolated-user', UserName: 'isolated-user', Email: 'isolated@example.test',
        Firstname: 'Isolated', Lastname: 'User', Acronym: 'IU', Permissions: [],
        HasPassword: true, Has2FA: false, TwoFactorMethods: [], EmailConfirmed: true,
        SecondFactorRequired: true, SecureSetupDueAt: '2020-01-01T00:00:00Z',
        AdministrationNeedsStepUp: false,
      })
      return
    }
    if (path === '/api/account/profile/request') {
      await json(block ? { [block]: true } : { Open: null, LastTerminal: null }, block ? 403 : 200)
      return
    }
    if (path === '/api/account/external-links' || path === '/api/account/passkey' || path === '/api/inbox') {
      await json(block ? { [block]: true } : [], block ? 403 : 200)
      return
    }
    if (path === '/api/account/mfa/status') { await json({ Enabled: false }); return }
    if (path === '/api/account/email-otp/status') { await json({ Enabled: false, HasEmail: true }); return }
    if (path === '/api/account/external-logins') { await json([]); return }
    if (path === '/api/auth/deletion-status') {
      await json({ IsPending: pending, IsDeleted: false, IsDataMasked: false, Initiator: pending ? 'SelfService' : null })
      return
    }
    if (path === '/api/auth/delete-account' && request.method() === 'POST') {
      deletionRequests++
      if (reauthenticate) {
        await json({ RequiresReauthentication: true }, 403)
      } else {
        pending = true
        await json({ Message: 'Scheduled by the fixture, not a real backend' })
      }
      return
    }
    if (path === '/api/account/step-up' && request.method() === 'POST') {
      await json({ RequiresFirstFactor: true, Methods: ['email'], Email: 'isolated@example.test' })
      return
    }
    if (path.startsWith('/api/')) {
      unexpected.push(`${request.method()} ${path}`)
      await json({ Message: 'Unexpected fixture request' }, 500)
      return
    }
    if (path === '/profile' || path === '/login') {
      await route.fulfill({ contentType: 'text/html', body: index })
      return
    }
    const file = resolve(dist, `.${decodeURIComponent(path)}`)
    if (file.startsWith(dist + sep)) {
      try {
        // One read, no stat first: it throws for a missing file or a directory.
        const body = readFileSync(file)
        await route.fulfill({ contentType: contentTypes[extname(file)] ?? 'application/octet-stream', body })
        return
      } catch { /* unknown static resource: fail closed below */ }
    }
    unexpected.push(path)
    await route.abort()
  })
  return { seen, unexpected, deletionRequests: () => deletionRequests }
}

async function openDeletion(page: Page) {
  await page.goto(`${origin}/profile`)
  await expect(page.getByRole('heading', { name: 'Account', exact: true })).toBeVisible()
  // The current UI has a German fallback for this untranslated menu key.
  await page.getByText('Datenschutz', { exact: true }).click()
  await page.getByRole('button', { name: 'Request Account Deletion', exact: true }).click()
}

for (const block of ['RequiresSecureSetup', 'RequiresStepUp', null]) {
  test(`real profile and header keep deletion usable with ${block ?? 'successful background reads'}`, async ({ page }) => {
    const f = await fixture(page, block)
    await openDeletion(page)
    await expect.poll(() => ['/api/account/profile/request', '/api/account/external-links', '/api/account/passkey', '/api/inbox']
      .every(path => f.seen.has(path))).toBe(true)
    await page.getByRole('button', { name: 'Schedule deletion', exact: true }).click()
    await expect.poll(f.deletionRequests).toBe(1)
    await expect(page.getByText('Your account is scheduled for deletion.', { exact: true })).toBeVisible()
    await expect(page).toHaveURL(`${origin}/profile`)
    expect(f.unexpected).toEqual([])
  })
}

test('an explicit deletion requiring fresh proof still navigates to reauthentication', async ({ page }) => {
  const f = await fixture(page, 'RequiresSecureSetup', true)
  await openDeletion(page)
  await page.getByRole('button', { name: 'Schedule deletion', exact: true }).click()
  await expect(page).toHaveURL(url => url.pathname === '/login'
    && url.searchParams.get('stepup') === '1'
    && url.searchParams.get('reauth') === '1'
    && url.searchParams.get('redirect') === '/profile')
  expect(f.deletionRequests()).toBe(1)
  expect(f.unexpected).toEqual([])
})
