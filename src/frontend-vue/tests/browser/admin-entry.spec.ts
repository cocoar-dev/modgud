import { test, expect, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { resolve, sep, extname } from 'node:path'

// Real SPA/router/layouts; all HTTP is simulated and unknown requests fail closed.
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

async function fixture(page: Page, permissions: string[], options: {
  positions?: boolean; stepUp?: boolean; anonymous?: boolean
} = {}) {
  const seen: string[] = []
  const unexpected: string[] = []
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.name))
  await page.addInitScript(() => {
    localStorage.setItem('language', 'en')
    localStorage.setItem('sidebar-collapsed', 'false')
  })
  await page.context().routeWebSocket('**/*', socket => socket.close())
  await page.context().route('**/*', async route => {
    const request = route.request()
    const url = new URL(request.url())
    if (url.origin !== origin) { unexpected.push('external request'); await route.abort(); return }
    const path = url.pathname
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body })
    if (path.startsWith('/signalr/')) { await json({}, 403); return }
    if (path === '/api/app-info') {
      await json({ Features: { PositionTerminals: options.positions ?? true, PageBuilder: false } })
      return
    }
    if (path === '/api/account/me') {
      await json({
        Id: 'admin-entry-user', UserName: 'reader', Email: 'reader@example.test',
        Permissions: permissions, EmailConfirmed: true, HasPassword: true,
        Has2FA: false, TwoFactorMethods: [], AdministrationNeedsStepUp: options.stepUp ?? false,
      }, options.anonymous ? 401 : 200)
      return
    }
    if (path === '/api/account/step-up') {
      await json({ RequiresFirstFactor: true, Methods: ['password'] }); return
    }
    if (path === '/api/account/external-logins') { await json([]); return }
    if (path === '/api/inbox') { await json([]); return }
    if (path === '/api/dashboard/layout') { await json({ User: [], RealmDefault: [] }); return }
    if (path === '/api/dashboard/stats') { await json({}); return }
    if (path === '/api/app') {
      // Invite-only users do not gain App read access just to populate a header.
      await json(permissions.includes('app:read') ? [] : { Message: 'Forbidden' },
        permissions.includes('app:read') ? 200 : 403)
      return
    }
    const reads: Record<string, string> = {
      '/api/service-account': 'service-account:read',
      '/api/position': 'position:read',
      '/api/admin/invite-codes': 'invite-code:read',
      '/api/admin/jobs': 'scheduled-job:read',
      '/api/admin/audit': 'audit-log:read',
    }
    if (reads[path] && request.method() === 'GET') {
      seen.push(path)
      await json(permissions.includes(reads[path]!) ? [] : { Message: 'Forbidden' },
        permissions.includes(reads[path]!) ? 200 : 403)
      return
    }
    if (path.startsWith('/api/')) {
      unexpected.push(`${request.method()} ${path}`)
      await json({ Message: 'Unexpected request' }, 403); return
    }
    const file = resolve(dist, `.${decodeURIComponent(path)}`)
    if (file.startsWith(dist + sep)) {
      try {
        // One read, no stat first: it throws for a missing file or a directory.
        const body = readFileSync(file)
        await route.fulfill({ body, contentType: mime[extname(file)] ?? 'application/octet-stream' })
        return
      } catch { /* SPA history fallback below */ }
    }
    if (request.isNavigationRequest()) { await route.fulfill({ body: index, contentType: 'text/html' }); return }
    unexpected.push(path); await route.abort()
  })
  return { seen, unexpected, errors }
}

const readers = [
  { permission: 'service-account:read', path: '/admin/service-accounts', api: '/api/service-account' },
  { permission: 'position:read', path: '/admin/positions', api: '/api/position' },
  { permission: 'invite-code:read', path: '/admin/invite-codes', api: '/api/admin/invite-codes' },
  { permission: 'scheduled-job:read', path: '/admin/scheduled-jobs', api: '/api/admin/jobs' },
  { permission: 'audit-log:read', path: '/admin/logs', api: '/api/admin/audit' },
]
for (const reader of readers) {
  test(`${reader.permission} alone: entry, own landing, direct link and hidden foreign sidebar`, async ({ page }) => {
    const f = await fixture(page, [reader.permission])
    await page.goto(origin + '/admin?entry=delegated')
    await expect(page).toHaveURL(origin + reader.path + '?entry=delegated')
    await expect(page.locator(`.sub-nav a[href="${reader.path}"]`)).toBeVisible()
    await expect(page.locator('.sub-nav a[href="/admin/users"]')).toHaveCount(0)
    await expect(page.locator('.sub-nav a')).toHaveCount(1)
    await expect(page.getByText('Administration', { exact: true }).first()).toBeVisible()
    await expect.poll(() => f.seen.includes(reader.api)).toBe(true)
    await page.goto(origin + reader.path)
    await expect(page).toHaveURL(origin + reader.path)
    await expect(page.locator(`.sub-nav a[href="${reader.path}"]`)).toBeVisible()
    // Actual top-level click, not just calling the landing helper.
    await page.locator('.coar-sidebar-item').filter({ hasText: 'Administration' }).click()
    await expect(page).toHaveURL(origin + reader.path)
    expect(f.unexpected).toEqual([])
    expect(f.errors).toEqual([])
  })
}

for (const options of [
  { permissions: [], name: 'no grants' },
  { permissions: ['service-account:write'], name: 'write without read' },
  { permissions: ['position:read'], positions: false, name: 'disabled position feature' },
]) {
  test(`${options.name}: no admin entry and deep link returns to dashboard`, async ({ page }) => {
    const f = await fixture(page, options.permissions, options)
    await page.goto(origin + '/admin/service-accounts')
    await expect(page).toHaveURL(origin + '/dashboard')
    await expect(page.getByText('Administration', { exact: true })).toHaveCount(0)
    expect(f.seen).toEqual([])
    expect(f.unexpected).toEqual([])
    expect(f.errors).toEqual([])
  })
}

test('delegated reader still requires administration step-up before opening its page', async ({ page }) => {
  const f = await fixture(page, ['service-account:read'], { stepUp: true })
  await page.goto(origin + '/admin/service-accounts?entry=stepup')
  await expect(page).toHaveURL(url => url.pathname === '/login'
    && url.searchParams.get('stepup') === '1'
    && url.searchParams.get('for') === 'admin'
    && url.searchParams.get('redirect') === '/admin/service-accounts?entry=stepup')
  expect(f.seen).toEqual([])
  expect(f.unexpected).toEqual([])
  expect(f.errors).toEqual([])
})

test('anonymous direct link still requires login with destination preserved', async ({ page }) => {
  const f = await fixture(page, [], { anonymous: true })
  await page.goto(origin + '/admin/service-accounts')
  await expect(page).toHaveURL(url => url.pathname === '/login'
    && url.searchParams.get('redirect') === '/admin/service-accounts')
  expect(f.seen).toEqual([])
  expect(f.unexpected).toEqual([])
  expect(f.errors).toEqual([])
})
