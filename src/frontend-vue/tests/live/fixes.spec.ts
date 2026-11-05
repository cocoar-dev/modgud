import { test, expect, type Page, type BrowserContext, type WebSocketRoute } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { resolve, sep, extname } from 'node:path'

type Login = { UserName: string; Password: string }
const fixturePath = process.env.MODGUD_LIVE_FIXTURE
const buildDir = process.env.MODGUD_BROWSER_DIST
if (!fixturePath || !buildDir) throw new Error('Run through LiveFixBrowserTests with an isolated Vite build.')
const fixture: { BaseUrl: string; InboxId: string; Creator: Login; Reader: Login; Profile: Login; Admin: Login } =
  JSON.parse(readFileSync(fixturePath, 'utf8'))
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

async function prepare(context: BrowserContext, login: Login) {
  context.setDefaultTimeout(10_000)
  // Permit only this test origin to reach its loopback backend. In particular,
  // route.fulfill() serves the SPA without a network-derived address space;
  // Chromium can therefore require LNA permission for its real WebSocket.
  // Do not disable browser security globally or change the product hub policy.
  await context.grantPermissions(['local-network-access'], { origin })
  await context.addInitScript(() => localStorage.setItem('language', 'en'))
  await context.route('**/*', async route => {
    const url = new URL(route.request().url())
    if (url.origin !== origin) { await route.abort(); return }
    // All API and SignalR traffic reaches the real backend, without substitution.
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
      } catch { /* history route below */ }
    }
    if (route.request().isNavigationRequest()) {
      await route.fulfill({ body: index, contentType: 'text/html' }); return
    }
    await route.abort()
  })
  const response = await context.request.post(`${origin}/api/account/login`, { data: login })
  expect(response.status()).toBe(200)
  const me = await context.request.get(`${origin}/api/account/me`)
  expect(me.status()).toBe(200)
  expect((await me.json()).UserName.toLowerCase()).toBe(login.UserName)
}

async function storeAccounts(page: Page): Promise<unknown[]> {
  return page.evaluate(() => {
    // Read the actual Pinia store; do not replace createEntity or HTTP/feeds.
    const app = (document.querySelector('#app') as any).__vue_app__
    const store = app.config.globalProperties.$pinia._s.get('serviceAccount')
    if (!store) throw new Error('Service-account store not mounted')
    return JSON.parse(JSON.stringify(store.entities))
  })
}

async function inboxState(page: Page) {
  return page.evaluate(() => {
    const app = (document.querySelector('#app') as any).__vue_app__
    const store = app.config.globalProperties.$pinia._s.get('inbox')
    if (!store) throw new Error('Inbox store not mounted')
    return { ids: store.items.map((item: any) => item.Id) as string[],
      loadFailed: store.loadFailed as boolean, allLoaded: store.allLoaded as boolean }
  })
}

test('creator dialog discloses once; read-only subscriber and both actual stores stay secret-free', async ({ browser }) => {
  const creatorContext = await browser.newContext()
  const readerContext = await browser.newContext()
  try {
    await prepare(creatorContext, fixture.Creator)
    await prepare(readerContext, fixture.Reader)
    await readerContext.addInitScript(() => localStorage.setItem('sidebar-collapsed', 'false'))
    const readerMe = await (await readerContext.request.get(`${origin}/api/account/me`)).json()
    expect(readerMe.Permissions).toEqual(['service-account:read'])
    expect((await readerContext.request.get(`${origin}/api/service-account`)).status()).toBe(200)
    expect((await readerContext.request.get(`${origin}/api/admin/oauth/scopes`)).status()).toBe(403)
    const creator = await creatorContext.newPage()
    const reader = await readerContext.newPage()
    const frames: string[] = []
    const diagnostics: string[] = []
    reader.on('console', message => {
      if (message.type() === 'error') diagnostics.push(message.text().slice(0, 400).replace(/([?&]id=)[^&\s]+/g, '$1[REDACTED]'))
    })
    reader.on('response', response => {
      const path = new URL(response.url()).pathname
      if (path.startsWith('/signalr/')) diagnostics.push(`${response.status()} ${path}`)
    })
    reader.on('websocket', socket => socket.on('framereceived', frame => frames.push(String(frame.payload))))
    await reader.goto(`${origin}/admin`)
    await expect(reader).toHaveURL(`${origin}/admin/service-accounts`)
    await expect(reader.locator('.sub-nav a[href="/admin/service-accounts"]')).toBeVisible()
    await expect(reader.locator('.sub-nav a[href="/admin/users"]')).toHaveCount(0)
    await reader.locator('.coar-sidebar-item').filter({ hasText: 'Administration' }).click()
    await expect(reader).toHaveURL(`${origin}/admin/service-accounts`)
    await creator.goto(`${origin}/admin/service-accounts`)

    // Correlated harmless creates prove the read subscription is active, rather
    // than assuming that opening a WebSocket means authorization has completed.
    let probeId = ''
    try { await expect(async () => {
      const response = await creatorContext.request.post(`${origin}/api/service-account`, {
        data: { AccountName: `probe-${crypto.randomUUID().slice(0, 12)}` },
      })
      expect(response.status()).toBe(200)
      probeId = (await response.json()).Id
      await expect.poll(() => frames.some(frame => frame.includes(probeId)), { timeout: 1500 }).toBe(true)
    }).toPass({ timeout: 10_000, intervals: [200] }) } catch (error) {
      // Before any credential is issued: no secrets in these probe diagnostics.
      console.log('Reader subscription diagnostics', JSON.stringify({ diagnostics, frames: frames.slice(0, 10) }))
      throw error
    }
    await expect.poll(async () => (await storeAccounts(reader)).some((a: any) => a.Id === probeId)).toBe(true)

    await creator.getByRole('button', { name: 'Create', exact: true }).click()
    const accountName = `live-${crypto.randomUUID().slice(0, 12)}`
    await creator.getByPlaceholder('ci.build-agent, integrations.acme, …').fill(accountName)
    await creator.getByRole('button', { name: 'Add OAuth client', exact: true }).click()
    await creator.locator('.modal-container').last().getByRole('button', { name: /Übernehmen|Apply/, exact: true }).click()
    const createResponse = creator.waitForResponse(r => new URL(r.url()).pathname === '/api/service-account' && r.request().method() === 'POST')
    await creator.locator('.modal-container').last().getByRole('button', { name: 'Create', exact: true }).click()
    const response = await createResponse
    expect(response.status()).toBe(200)
    const created = await response.json()
    const secret: string = created.InitialCredential?.ClientSecret
    expect(typeof secret === 'string' && secret.length > 0).toBe(true)
    // Boolean assertions avoid printing the actual credential on a failure.
    await expect.poll(async () => (await creator.locator('.modal-container code').allTextContents()).includes(secret)).toBe(true)
    expect((await creator.locator('.modal-container code').allTextContents())
      .includes(created.InitialCredential.Credential.ClientId)).toBe(true)
    await expect.poll(() => frames.some(frame => frame.includes(created.Id))).toBe(true)
    expect(frames.every(frame => !frame.includes(secret))).toBe(true)
    const event = frames.flatMap(frame => frame.split('\u001e').filter(Boolean).map(text => JSON.parse(text)))
      .find(message => message.type === 2 && JSON.stringify(message.item).includes(created.Id))
    expect(JSON.stringify(event.item).includes(secret)).toBe(false)
    expect(event.item.Subject).toBe('ServiceAccount')
    expect(event.item.Action).toBe('Created')
    expect(Object.hasOwn(event.item.Payload[0], 'InitialCredential')).toBe(false)
    for (const page of [creator, reader]) {
      await expect.poll(async () => (await storeAccounts(page)).some((a: any) => a.Id === created.Id)).toBe(true)
      expect(JSON.stringify(await storeAccounts(page)).includes(secret)).toBe(false)
    }
    const snapshot = await readerContext.request.get(`${origin}/api/service-account`)
    expect(snapshot.status()).toBe(200)
    expect((await snapshot.text()).includes(secret)).toBe(false)
    // A read-only caller must not have acquired write access along the way.
    expect((await readerContext.request.post(`${origin}/api/service-account`, { data: { AccountName: 'forbidden-create' } })).status()).toBe(403)
    await creator.locator('.modal-container').getByRole('button', { name: /Done|Fertig/, exact: true }).click()
    await creator.getByText(accountName, { exact: true }).dblclick()
    await expect(creator.getByPlaceholder('ci.build-agent, integrations.acme, …')).toHaveValue(accountName)
    expect((await creator.locator('body').innerText()).includes(secret)).toBe(false)
    // Start from the list after a full reload, then explicitly reopen. Modal
    // fragment restoration is not the one-time-disclosure contract under test.
    await creator.goto(`${origin}/admin/service-accounts`)
    await creator.getByText(accountName, { exact: true }).dblclick()
    await expect(creator.getByPlaceholder('ci.build-agent, integrations.acme, …')).toHaveValue(accountName)
    expect((await creator.locator('body').innerText()).includes(secret)).toBe(false)
  } finally {
    await creatorContext.close(); await readerContext.close()
  }
})

test('no admin grants: actual login retains API denials and cannot enter the admin shell', async ({ browser }) => {
  const context = await browser.newContext()
  const anonymous = await browser.newContext()
  try {
    await prepare(context, fixture.Profile)
    const me = await (await context.request.get(`${origin}/api/account/me`)).json()
    expect(me.Permissions).toEqual([])
    expect((await context.request.get(`${origin}/api/service-account`)).status()).toBe(403)
    expect((await context.request.get(`${origin}/api/admin/oauth/scopes`)).status()).toBe(403)
    expect((await anonymous.request.get(`${origin}/api/service-account`)).status()).toBe(401)
    const page = await context.newPage()
    const errors: string[] = []
    page.on('pageerror', error => errors.push(error.name))
    for (const path of ['/admin', '/admin/service-accounts']) {
      await page.goto(origin + path)
      await expect(page).toHaveURL(`${origin}/dashboard`)
      await expect(page.locator('.coar-sidebar-item').filter({ hasText: 'Administration' })).toHaveCount(0)
      await expect(page.locator('.sub-nav')).toHaveCount(0)
    }
    expect(errors).toEqual([])
  } finally {
    await context.close(); await anonymous.close()
  }
})

test('real inbox reconnect and single-factor profile deletion retain MFA and proof boundaries', async ({ browser }) => {
  const admin = await browser.newContext()
  const context = await browser.newContext()
  try {
    await prepare(admin, fixture.Admin)
    await prepare(context, fixture.Profile)
    const sockets: { client: WebSocketRoute; server: WebSocketRoute }[] = []
    let handshakes = 0
    await context.routeWebSocket('**/signalr/ui**', socket => {
      const server = socket.connectToServer()
      server.onMessage(message => {
        if (String(message).includes('{}\u001e')) handshakes++
        socket.send(message) // Forward the real frame without modification.
      })
      sockets.push({ client: socket, server })
    })
    const page = await context.newPage()
    let inboxResponses = 0
    const pageErrors: string[] = []
    page.on('pageerror', error => pageErrors.push(error.message))
    page.on('response', response => {
      if (new URL(response.url()).pathname === '/api/inbox') inboxResponses++
    })
    await page.goto(`${origin}/profile`)
    await expect.poll(() => sockets.length).toBe(1)
    await expect.poll(() => handshakes).toBe(1)
    await expect.poll(() => inboxResponses).toBeGreaterThan(0)
    await expect.poll(async () => (await inboxState(page)).ids.includes(fixture.InboxId)).toBe(true)
    expect((await inboxState(page)).loadFailed).toBe(false)
    console.log('Profile: initial hub handshake and inbox response observed')
    const beforeHandshake = handshakes
    const reloaded = page.waitForRequest(request =>
      new URL(request.url()).pathname === '/api/inbox' && handshakes > beforeHandshake)
    await sockets[0]!.server.close({ code: 1012, reason: 'isolated reconnect regression' })
    await sockets[0]!.client.close({ code: 1012, reason: 'isolated reconnect regression' })
    await expect.poll(() => sockets.length).toBeGreaterThan(1)
    await expect.poll(() => handshakes).toBeGreaterThan(beforeHandshake)
    expect((await (await reloaded).response())?.status()).toBe(200)
    console.log('Profile: successful reconnect and correlated inbox reload observed')
    expect(pageErrors).toEqual([])

    // Let the next reconnect authenticate while Single is still allowed, then
    // hold its real REST reload until the realm policy is changed. This exercises
    // a genuine onReconnect -> loadAll -> server MFA-403 without forging a body
    // or pretending an MFA-refused hub handshake was a successful reconnect.
    let reloadArrived!: () => void
    const arrived = new Promise<void>(resolve => { reloadArrived = resolve })
    let releaseReload!: () => void
    const release = new Promise<void>(resolve => { releaseReload = resolve })
    await context.route(`${origin}/api/inbox`, async route => {
      reloadArrived()
      await release
      await route.continue()
    })
    const current = sockets.at(-1)!
    await current.server.close({ code: 1012, reason: 'isolated MFA reload regression' })
    await current.client.close({ code: 1012, reason: 'isolated MFA reload regression' })
    await Promise.race([arrived, page.waitForTimeout(10_000).then(() => { throw new Error('No inbox reload after second reconnect') })])
    const patch = await admin.request.patch(`${origin}/api/admin/realm-settings`, {
      data: { SignIn: { MinimumLevel: 'Multi', EmailCode: true } },
    })
    expect(patch.status()).toBe(200)
    const refusedReload = page.waitForResponse(r => new URL(r.url()).pathname === '/api/inbox' && r.status() === 403)
    releaseReload()
    expect((await (await refusedReload).json()).RequiresSecureSetup).toBe(true)
    await expect(page).toHaveURL(`${origin}/profile`)
    await expect.poll(async () => (await inboxState(page)).loadFailed).toBe(true)
    expect((await inboxState(page)).allLoaded).toBe(false)
    expect((await inboxState(page)).ids).toEqual([])
    expect(pageErrors).toEqual([])
    await page.locator('.bell-btn').click()
    await expect(page.getByText('Notifications could not be refreshed. Reconnect or reload the page to try again.', { exact: true })).toBeVisible()
    await expect(page.getByText('No notifications', { exact: true })).toHaveCount(0)
    await page.locator('.bell-btn').click()
    await context.unroute(`${origin}/api/inbox`)

    // Restore the actual policy, then recover via another real reconnect rather
    // than patching the store or substituting a successful REST response.
    expect((await admin.request.patch(`${origin}/api/admin/realm-settings`, {
      data: { SignIn: { MinimumLevel: 'Single' } },
    })).status()).toBe(200)
    const recoveryHandshake = handshakes
    const recovered = page.waitForRequest(request =>
      new URL(request.url()).pathname === '/api/inbox' && handshakes > recoveryHandshake)
    const recoverySocket = sockets.at(-1)!
    await recoverySocket.server.close({ code: 1012, reason: 'isolated reload recovery' })
    await recoverySocket.client.close({ code: 1012, reason: 'isolated reload recovery' })
    expect((await (await recovered).response())?.status()).toBe(200)
    await expect.poll(async () => (await inboxState(page)).ids.includes(fixture.InboxId)).toBe(true)
    expect((await inboxState(page)).loadFailed).toBe(false)
    expect((await inboxState(page)).allLoaded).toBe(true)
    expect(pageErrors).toEqual([])
    await page.locator('.bell-btn').click()
    await expect(page.getByText('Notifications could not be refreshed. Reconnect or reload the page to try again.', { exact: true })).toHaveCount(0)
    await page.locator('.bell-btn').click()
    expect((await admin.request.patch(`${origin}/api/admin/realm-settings`, {
      data: { SignIn: { MinimumLevel: 'Multi', EmailCode: true } },
    })).status()).toBe(200)
    const seen = new Map<string, number>()
    page.on('response', response => seen.set(new URL(response.url()).pathname, response.status()))
    await page.goto(`${origin}/profile`)
    try {
      await expect(page.getByRole('heading', { name: 'Account', exact: true })).toBeVisible()
    } catch (error) {
      console.log('Profile reload diagnostics', JSON.stringify({ path: new URL(page.url()).pathname, responses: [...seen] }))
      throw error
    }
    await page.getByText('Datenschutz', { exact: true }).click()
    for (const path of ['/api/account/profile/request', '/api/account/external-links', '/api/account/passkey', '/api/inbox']) {
      await expect.poll(() => seen.get(path)).toBe(403)
      const response = await context.request.get(origin + path)
      expect((await response.json()).RequiresSecureSetup).toBe(true)
    }
    const mutation = await context.request.put(`${origin}/api/account/profile/request`, { data: { Firstname: 'Forbidden' } })
    expect(mutation.status()).toBe(403)
    await page.getByRole('button', { name: 'Request Account Deletion', exact: true }).click()
    const deletion = page.waitForResponse(response =>
      new URL(response.url()).pathname === '/api/auth/delete-account' && response.request().method() === 'POST')
    await page.getByRole('button', { name: 'Schedule deletion', exact: true }).click()
    expect((await deletion).status()).toBe(200)
    // The real pending notice appends a deadline; unlike the isolated fixture,
    // its complete text is not just the leading status sentence.
    await expect(page.getByRole('button', { name: 'Cancel deletion', exact: true })).toBeVisible()
    await expect(page).toHaveURL(`${origin}/profile`)
    const status = await context.request.get(`${origin}/api/auth/deletion-status`)
    expect((await status.json()).IsPending).toBe(true)
    expect((await context.request.post(`${origin}/api/auth/cancel-deletion`)).ok()).toBe(true)
    expect((await (await context.request.get(`${origin}/api/auth/deletion-status`)).json()).IsPending).toBe(false)
    expect(pageErrors).toEqual([])
  } finally {
    await admin.close(); await context.close()
  }
})
