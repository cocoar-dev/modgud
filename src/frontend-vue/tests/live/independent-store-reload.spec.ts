import { test, expect, type BrowserContext, type Page, type WebSocketRoute } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { resolve, sep, extname } from 'node:path'

// Same opt-in real host as fixes.spec.ts. No existing application endpoint,
// transport response, permission decision or store implementation is replaced.
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
const warning = 'The list could not be refreshed. Reconnect or reload the page to try again.'
const cases = [
  { name: 'applications', store: 'applications', list: 'apps', route: '/admin/apps', api: '/api/app', permission: 'app', stream: 'AppActions.Subscribe' },
  { name: 'OAuth clients', store: 'oauth-client', list: 'clients', route: '/admin/oauth/clients', api: '/api/admin/oauth/clients', permission: 'oauth-client', stream: 'OAuthClientActions.Subscribe' },
  { name: 'OAuth scopes', store: 'oauth-scope', list: 'scopes', route: '/admin/oauth/scopes', api: '/api/admin/oauth/scopes', permission: 'oauth-scope', stream: 'OAuthScopeActions.Subscribe' },
  { name: 'invite codes', store: 'invite-code', list: 'codes', route: '/admin/invite-codes', api: '/api/admin/invite-codes', permission: 'invite-code', stream: 'InviteCodeActions.Subscribe' },
]

async function prepare(context: BrowserContext) {
  await context.grantPermissions(['local-network-access'], { origin })
  await context.addInitScript(() => localStorage.setItem('language', 'en'))
  await context.route('**/*', async route => {
    const url = new URL(route.request().url())
    if (url.origin !== origin) { await route.abort(); return }
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/signalr/')) {
      await route.continue(); return
    }
    const file = resolve(dist, `.${decodeURIComponent(url.pathname)}`)
    if (file.startsWith(dist + sep)) {
      try {
        // One read, no stat first: it throws for a missing file or a directory.
        const body = readFileSync(file)
        await route.fulfill({ body, contentType: mime[extname(file)] ?? 'application/octet-stream' })
        return
      } catch { /* SPA history fallback */ }
    }
    if (route.request().isNavigationRequest()) {
      await route.fulfill({ body: index, contentType: 'text/html' }); return
    }
    await route.abort()
  })
}

async function state(page: Page, config: typeof cases[number]) {
  return page.evaluate(({ store, list }) => {
    const app = (document.querySelector('#app') as any).__vue_app__
    const sut = app?.config.globalProperties.$pinia._s.get(store)
    return sut ? { ids: sut[list].map((x: any) => x.Id) as string[],
      loaded: sut.loaded as boolean, loadFailed: sut.loadFailed as boolean } : null
  }, config)
}

for (const config of cases) {
  test(`${config.name}: real reconnect 200, permission 403 and recovery keep one subscription and visible failure`, async ({ browser }) => {
    const admin = await browser.newContext()
    const context = await browser.newContext()
    let role: any
    try {
      await prepare(admin)
      expect((await admin.request.post(`${origin}/api/account/login`, { data: fixture.Admin })).status()).toBe(200)
      // Undo the preceding profile test's policy arrangement; these four cases
      // examine real RBAC refusal, not an administration-MFA bypass.
      expect((await admin.request.patch(`${origin}/api/admin/realm-settings`, {
        data: { SignIn: { MinimumLevel: 'Single', AdministrationMinimumLevel: 'Single' } },
      })).status()).toBe(200)
      const apps = await (await admin.request.get(`${origin}/api/app`)).json()
      const roles = await (await admin.request.get(`${origin}/api/role`)).json()
      role = roles.find((r: any) => r.Name === 'Live creator')
      // Both Modgud and the control-plane App are system records. Resolve the
      // role's actual catalog FK rather than picking the first IsSystem row.
      let system = apps.find((app: any) => app.Id === role?.AppId)
      expect(Boolean(system && role)).toBe(true)
      // Existing seeder omits invite-code grants. Arrange this test-owned
      // catalog explicitly through the normal admin API; do not repair or
      // bypass the production permission service as part of gnsm.
      if (!system.Permissions.some((p: any) => p.Resource === 'invite-code' && p.Action === 'read')) {
        const catalogUpdate = await admin.request.put(`${origin}/api/app/${system.Id}`, { data: {
          DisplayName: system.DisplayName, Description: system.Description,
          Permissions: [...system.Permissions, { Resource: 'invite-code', Action: 'read' }],
        } })
        expect(catalogUpdate.status()).toBe(200)
        system = await catalogUpdate.json()
      }
      const catalog = system.Permissions.filter((p: any) =>
        p.Action === 'read' && ['app', 'oauth-client', 'oauth-scope', 'invite-code', 'service-account'].includes(p.Resource))
      expect(catalog).toHaveLength(5)
      const granted = { ...role, PermissionIds: [...new Set([...role.PermissionIds, ...catalog.map((p: any) => p.Id)])] }
      expect((await admin.request.put(`${origin}/api/role/${role.Id}`, { data: granted })).status()).toBe(200)

      // Persist nonempty snapshots; do not assert on or retain minted secrets.
      const suffix = `${Date.now()}-${config.store}`
      if (config.store === 'oauth-client') {
        expect((await admin.request.post(origin + config.api, { data: {
          ClientId: suffix, DisplayName: suffix, ClientType: 'public',
          AllowedGrantTypes: ['authorization_code'], RedirectUris: ['https://client.modgud.test/callback'],
        } })).ok()).toBe(true)
      } else if (config.store === 'invite-code') {
        expect((await admin.request.post(`${origin}/api/app/${system.Id}/invite-codes`, { data: { Count: 1 } })).ok()).toBe(true)
      } else if (config.store === 'oauth-scope') {
        expect((await admin.request.post(origin + config.api, { data: { Name: suffix, DisplayName: suffix } })).ok()).toBe(true)
      }

      await prepare(context)
      expect((await context.request.post(`${origin}/api/account/login`, { data: fixture.Creator })).status()).toBe(200)
      const sockets: { client: WebSocketRoute; server: WebSocketRoute }[] = []
      let handshakes = 0
      let subscriptions = 0
      await context.routeWebSocket('**/signalr/ui**', socket => {
        const server = socket.connectToServer()
        server.onMessage(message => {
          if (String(message).includes('{}\u001e')) handshakes++
          socket.send(message)
        })
        socket.onMessage(message => {
          // SignalARRR carries Method inside the StreamMessage envelope, not
          // as the native SignalR target. Count real invocations, not frames.
          for (const record of String(message).split('\u001e').filter(Boolean)) {
            const packet = JSON.parse(record)
            if (packet.type === 4 && packet.target === 'StreamMessage' &&
              packet.arguments?.[0]?.Method === config.stream) subscriptions++
          }
          server.send(message)
        })
        sockets.push({ client: socket, server })
      })
      const page = await context.newPage()
      const errors: string[] = []
      page.on('pageerror', error => errors.push(error.name)) // no credential-bearing messages
      let reads = 0
      page.on('request', request => {
        if (new URL(request.url()).pathname === config.api && request.method() === 'GET') reads++
      })
      await page.goto(origin + config.route)
      await expect.poll(async () => (await state(page, config))?.loaded).toBe(true)
      const initial = (await state(page, config))!
      expect(initial.ids.length).toBeGreaterThan(0)
      await expect.poll(() => handshakes).toBe(1)
      await expect.poll(() => subscriptions).toBe(1)
      const reconnect = async () => {
        const socket = sockets.at(-1)!
        await socket.server.close({ code: 1012, reason: 'isolated gnsm regression' })
        await socket.client.close({ code: 1012, reason: 'isolated gnsm regression' })
      }
      const reload = (status: number) => page.waitForResponse(response =>
        new URL(response.url()).pathname === config.api &&
        response.request().method() === 'GET' && response.status() === status)

      const firstReads = reads
      const successful = reload(200)
      await reconnect()
      await successful
      await expect.poll(() => subscriptions).toBe(2)
      await expect.poll(async () => (await state(page, config))?.loaded).toBe(true)
      expect(reads - firstReads).toBe(1)

      // Authenticate the hub under the grant, hold only its real REST reload,
      // revoke the target permission through the real role API, then forward.
      let arrived!: () => void
      const arrival = new Promise<void>(resolve => { arrived = resolve })
      let release!: () => void
      const held = new Promise<void>(resolve => { release = resolve })
      const listUrl = (url: URL) => url.pathname === config.api
      await context.route(listUrl, async route => {
        arrived()
        await held
        await route.continue()
      })
      const failedReads = reads
      await reconnect()
      await Promise.race([arrival, page.waitForTimeout(10_000).then(() => { throw new Error('Missing reconnect reload') })])
      const revoked = catalog.find((p: any) => p.Resource === config.permission).Id
      expect((await admin.request.put(`${origin}/api/role/${role.Id}`, {
        data: { ...granted, PermissionIds: granted.PermissionIds.filter((id: string) => id !== revoked) },
      })).status()).toBe(200)
      const refused = reload(403)
      release()
      await refused
      await expect.poll(async () => (await state(page, config))?.loadFailed).toBe(true)
      expect((await state(page, config))?.ids).toEqual([])
      expect((await state(page, config))?.loaded).toBe(false)
      await expect(page.getByText(warning, { exact: true }).first()).toBeVisible()
      await expect(page).toHaveURL(origin + config.route)
      expect(errors).toEqual([])
      expect(reads - failedReads).toBe(1)
      await context.unroute(listUrl)

      expect((await admin.request.put(`${origin}/api/role/${role.Id}`, { data: granted })).status()).toBe(200)
      const recoveryReads = reads
      const recovered = reload(200)
      await reconnect()
      await recovered
      await expect.poll(async () => (await state(page, config))?.loadFailed).toBe(false)
      expect((await state(page, config))?.loaded).toBe(true)
      expect((await state(page, config))?.ids).toEqual(expect.arrayContaining(initial.ids))
      await expect(page.getByText(warning, { exact: true })).toHaveCount(0)
      await expect.poll(() => subscriptions).toBe(4)
      expect(handshakes).toBe(4)
      await page.waitForTimeout(250)
      expect(reads - recoveryReads).toBe(1) // no retry loop / duplicate reload
      expect(errors).toEqual([])
    } finally {
      if (role) expect((await admin.request.put(`${origin}/api/role/${role.Id}`, { data: role })).status()).toBe(200)
      await context.close()
      await admin.close()
    }
  })
}
