import { test, expect, type APIRequestContext } from '@playwright/test'
import { apiLogin, uniqueSuffix } from './helpers'

/**
 * §21 of the manual checklist — permission gating end-to-end.
 *
 * Builds three non-admin users with different role/BoundTo configurations
 * and asserts both:
 *   - the **API** returns the expected 200/403 mix when each user calls
 *     gated endpoints, and
 *   - the **SPA sidebar** mirrors the same answers — items the user can't
 *     reach should not be in the rendered sidebar at all (hidden, not just
 *     dimmed).
 *
 * The integration test `PermissionResolutionTests` already exhausts the
 * gate logic at the HTTP-filter level (10 cases). This spec adds the SPA
 * perspective: the front-end has to consult `/me`, parse `Permissions`,
 * and gate sidebar items against the **same** strings the backend uses.
 * If the two ever drift, this is where it surfaces.
 */

const ADMIN_USER = 'admin'
const ADMIN_PASSWORD = 'ABC12abc!'
const TEST_PASSWORD = 'TestPass1234!'

// Each run builds fresh users/roles/groups with a random suffix so tests
// don't collide on a re-used DB. Phase A's smoke spec creates the admin
// once; we just authenticate as that admin to drive the setup work here.
const SUFFIX = uniqueSuffix()

test.describe.configure({ mode: 'serial' })

interface TestUser {
  userName: string
  userId: string
  password: string
}

/** Create a Person, set a password, return identifiers. */
async function createUserWithPassword(api: APIRequestContext, userName: string): Promise<TestUser> {
  const created = await (await api.post('/api/user', {
    data: {
      Firstname: 'Gate', Lastname: 'Test', Acronym: userName,
      Email: `${userName}@modgud.test`,
    },
  })).json()
  const passRes = await api.put(`/api/user/${created.Id}/password`, {
    data: { Password: TEST_PASSWORD },
  })
  if (!passRes.ok()) throw new Error(`set-password failed: ${passRes.status()} ${await passRes.text()}`)
  return { userName, userId: created.Id, password: TEST_PASSWORD }
}

/**
 * The modgud app's catalog, resolved once: its AppId plus a map from the bare
 * "<resource>:<action>" string to the catalog permission's ShortGuid Id. Roles
 * grant permissions by these FK ids in the catalog-FK model.
 */
interface ModgudCatalog {
  appId: string
  permId: Map<string, string>
}

async function loadModgudCatalog(api: APIRequestContext): Promise<ModgudCatalog> {
  const res = await api.get('/api/app')
  if (!res.ok()) throw new Error(`GET /api/app failed: ${res.status()} ${await res.text()}`)
  const apps = await res.json() as {
    Id: string; Slug: string; Permissions: { Id: string; Resource: string; Action: string }[]
  }[]
  const modgud = apps.find(a => a.Slug === 'modgud')
  if (!modgud) throw new Error('modgud system app not found in /api/app catalog')
  const permId = new Map<string, string>()
  for (const p of modgud.Permissions) permId.set(`${p.Resource}:${p.Action}`, p.Id)
  return { appId: modgud.Id, permId }
}

/**
 * Create an app-linked Role in the catalog-FK model: AppId (ShortGuid) +
 * PermissionIds (ShortGuid FKs into that App's catalog). `permissionKeys` are
 * bare "<resource>:<action>" strings resolved against the catalog.
 */
async function createRole(
  api: APIRequestContext,
  name: string,
  catalog: ModgudCatalog,
  permissionKeys: string[],
): Promise<{ Id: string }> {
  const permissionIds = permissionKeys.map(key => {
    const id = catalog.permId.get(key)
    if (!id) throw new Error(`permission '${key}' not found in modgud catalog`)
    return id
  })
  const res = await api.post('/api/role', {
    data: { Name: name, AppId: catalog.appId, IsRealmAdmin: false, PermissionIds: permissionIds },
  })
  if (!res.ok()) throw new Error(`create role failed: ${res.status()} ${await res.text()}`)
  return await res.json()
}

/** Create a Group binding the given member + role + BoundTo. */
async function createGroup(
  api: APIRequestContext,
  name: string,
  memberId: string,
  roleId: string,
  boundTo: string[],
): Promise<{ id: string }> {
  const res = await api.post('/api/group', {
    data: {
      Name: name,
      MemberIds: [memberId],
      RoleIds: [roleId],
      MembershipMode: 'Manual',
      EmailMode: 'Shared',
      BoundTo: boundTo,
    },
  })
  if (!res.ok()) throw new Error(`create group failed: ${res.status()} ${await res.text()}`)
  return await res.json()
}

/**
 * Build a user with exactly one role-via-group, returning the credentials.
 * `permissionKeys` are bare "<resource>:<action>" catalog strings (e.g.
 * "user:read", "app:admin"); createRole resolves them to catalog FK ids.
 */
async function buildScopedUser(
  api: APIRequestContext,
  catalog: ModgudCatalog,
  label: string,
  permissionKeys: string[],
  boundTo: string[],
): Promise<TestUser> {
  const userName = `${label}-${SUFFIX}`
  const user = await createUserWithPassword(api, userName)
  const role = await createRole(api, `Role-${userName}`, catalog, permissionKeys)
  await createGroup(api, `Group-${userName}`, user.userId, role.Id, boundTo)
  return user
}

let readOnlyUser: TestUser
let multiReadUser: TestUser
let resourceAdminUser: TestUser

test.beforeAll(async ({ request }) => {
  // Authenticate the admin in this request context so the create-user etc
  // calls below carry the realm:admin cookie.
  const login = await request.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!login.ok()) throw new Error(`admin login failed: ${login.status()}`)

  const catalog = await loadModgudCatalog(request)

  // user 1: user:read only — sees the "Users" sidebar item and nothing else.
  readOnlyUser = await buildScopedUser(
    request, catalog, 'readonly', ['user:read'], ['modgud'],
  )

  // user 2: user:read + permission-role:read — additive catalog grants compose;
  // sees "Users" and "Roles" but nothing else.
  multiReadUser = await buildScopedUser(
    request, catalog, 'multiread', ['user:read', 'permission-role:read'], ['modgud'],
  )

  // user 3: app:admin — the resource-wide bypass tier. Holding "<resource>:admin"
  // grants every action on that resource (here app:read/write) and nothing on
  // other resources. app:admin ships in the seeded modgud catalog (user:admin
  // does not), so we exercise the bypass on the resource that has an admin
  // action. The full bypass cascade (resource-admin, realm-admin, BoundTo
  // scoping) is exhaustively covered at the HTTP-filter level by
  // PermissionResolutionTests; here we prove one tier round-trips the prod image.
  resourceAdminUser = await buildScopedUser(
    request, catalog, 'resourceadmin', ['app:admin'], ['modgud'],
  )
})

test.describe('§21 read-only user (modgud:user:read)', () => {
  test('GET /api/user → 200, GET /api/role → 403', async ({ request }) => {
    await request.post('/api/account/login', {
      data: { UserName: readOnlyUser.userName, Password: readOnlyUser.password, RememberMe: false },
    })

    const users = await request.get('/api/user')
    expect(users.status()).toBe(200)

    const roles = await request.get('/api/role')
    expect(roles.status()).toBe(403)

    const groups = await request.get('/api/group')
    expect(groups.status()).toBe(403)

    const oauth = await request.get('/api/admin/oauth/clients')
    expect(oauth.status()).toBe(403)
  })

  test('SPA sidebar shows only the gated items the user has access to', async ({ page }) => {
    await apiLogin(page, readOnlyUser.userName, readOnlyUser.password)
    await page.goto('/admin/users')
    await expect(page.getByRole('menuitem', { name: /Benutzer|Users/i })).toBeVisible({ timeout: 10_000 })
    // Sidebar items the read-only user must NOT see — they should be hidden.
    await expect(page.getByRole('menuitem', { name: /Rollen|Roles/i })).not.toBeVisible()
    await expect(page.getByRole('menuitem', { name: /OAuth-Clients|OAuth Clients/i })).not.toBeVisible()
    await expect(page.getByRole('menuitem', { name: /^Realms$/i })).not.toBeVisible()
  })
})

test.describe('§21 multi-read user (user:read + permission-role:read)', () => {
  test('additive catalog grants compose — /api/user + /api/role 200, others 403', async ({ request }) => {
    await request.post('/api/account/login', {
      data: { UserName: multiReadUser.userName, Password: multiReadUser.password, RememberMe: false },
    })

    expect((await request.get('/api/user')).status()).toBe(200)
    expect((await request.get('/api/role')).status()).toBe(200)
    // No authorization-group:read / oauth-client:read grant.
    expect((await request.get('/api/group')).status()).toBe(403)
    expect((await request.get('/api/admin/oauth/clients')).status()).toBe(403)
  })

  test('SPA sidebar shows Users + Roles, hides the rest', async ({ page }) => {
    await apiLogin(page, multiReadUser.userName, multiReadUser.password)
    await page.goto('/admin/users')
    await expect(page.getByRole('menuitem', { name: /Benutzer|Users/i })).toBeVisible({ timeout: 10_000 })
    await expect(page.getByRole('menuitem', { name: /Rollen|Roles/i })).toBeVisible()
    await expect(page.getByRole('menuitem', { name: /OAuth-Clients|OAuth Clients/i })).not.toBeVisible()
    await expect(page.getByRole('menuitem', { name: /^Realms$/i })).not.toBeVisible()
  })
})

test.describe('§21 resource-admin user (app:admin bypass)', () => {
  test('app:admin bypasses every action on its resource only — /api/app 200, /api/user 403', async ({ request }) => {
    await request.post('/api/account/login', {
      data: { UserName: resourceAdminUser.userName, Password: resourceAdminUser.password, RememberMe: false },
    })

    // app:admin grants app:read via the resource-wide bypass tier.
    expect((await request.get('/api/app')).status()).toBe(200)
    // …but confers nothing on the user resource.
    expect((await request.get('/api/user')).status()).toBe(403)
  })
})

test.describe('§21 realm-admin (the seeded admin)', () => {
  test('every gated read endpoint returns 200', async ({ request }) => {
    await request.post('/api/account/login', {
      data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
    })
    expect((await request.get('/api/user')).status()).toBe(200)
    expect((await request.get('/api/role')).status()).toBe(200)
    expect((await request.get('/api/group')).status()).toBe(200)
    expect((await request.get('/api/admin/oauth/clients')).status()).toBe(200)
    expect((await request.get('/api/admin/oauth/scopes')).status()).toBe(200)
    expect((await request.get('/api/admin/oauth/apis')).status()).toBe(200)
    expect((await request.get('/api/admin/realms')).status()).toBe(200)
  })
})
