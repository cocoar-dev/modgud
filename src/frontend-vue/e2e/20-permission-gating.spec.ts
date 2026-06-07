import { test, expect, type APIRequestContext, type BrowserContext } from '@playwright/test'
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

/** Create a Role with the explicit 3-segment shape (App + Resource + bare actions). */
async function createRole(
  api: APIRequestContext,
  name: string,
  appSlug: string,
  resourceType: string,
  actions: string[],
): Promise<{ id: string }> {
  const res = await api.post('/api/role', {
    data: { Name: name, AppSlug: appSlug, ResourceType: resourceType, Permissions: actions },
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
 * `actions` are bare strings ("read", "admin", …); the role's AppSlug +
 * ResourceType expand them at resolution time. For fully-qualified strings
 * like "realm:admin" pass them as-is — they pass through unchanged.
 */
async function buildScopedUser(
  api: APIRequestContext,
  label: string,
  appSlug: string,
  resourceType: string,
  actions: string[],
  boundTo: string[],
): Promise<TestUser> {
  const userName = `${label}-${SUFFIX}`
  const user = await createUserWithPassword(api, userName)
  const role = await createRole(api, `Role-${userName}`, appSlug, resourceType, actions)
  await createGroup(api, `Group-${userName}`, user.userId, (role as any).Id, boundTo)
  return user
}

let readOnlyUser: TestUser
let resourceAdminUser: TestUser
let appAdminUser: TestUser

test.beforeAll(async ({ request }) => {
  // Authenticate the admin in this request context so the create-user etc
  // calls below carry the realm:admin cookie.
  const login = await request.post('/api/account/login', {
    data: { UserName: ADMIN_USER, Password: ADMIN_PASSWORD, RememberMe: false },
  })
  if (!login.ok()) throw new Error(`admin login failed: ${login.status()}`)

  // user 1: modgud:user:read only — should see "Users" sidebar item only
  readOnlyUser = await buildScopedUser(
    request, 'readonly', 'modgud', 'user', ['read'], ['modgud'],
  )

  // user 2: modgud:user:admin (resource-admin bypass) — Users + nothing else
  resourceAdminUser = await buildScopedUser(
    request, 'resourceadmin', 'modgud', 'user', ['admin'], ['modgud'],
  )

  // user 3: modgud:admin (app-admin bypass) — every modgud resource
  appAdminUser = await buildScopedUser(
    request, 'appadmin', 'modgud', 'app', ['modgud:admin'], ['modgud'],
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

test.describe('§21 resource-admin user (modgud:user:admin)', () => {
  test('GET /api/user works, GET /api/role does NOT — bypass is per-resource', async ({ request }) => {
    await request.post('/api/account/login', {
      data: { UserName: resourceAdminUser.userName, Password: resourceAdminUser.password, RememberMe: false },
    })

    const users = await request.get('/api/user')
    expect(users.status()).toBe(200)

    // user:admin bypasses every action ON USER, not on other resources.
    // /api/role is gated by modgud:permission-role:read which we don't have.
    const roles = await request.get('/api/role')
    expect(roles.status()).toBe(403)
  })
})

test.describe('§21 app-admin user (modgud:admin)', () => {
  test('GET /api/user, /api/role, /api/group all 200 — app-wide bypass', async ({ request }) => {
    await request.post('/api/account/login', {
      data: { UserName: appAdminUser.userName, Password: appAdminUser.password, RememberMe: false },
    })

    expect((await request.get('/api/user')).status()).toBe(200)
    expect((await request.get('/api/role')).status()).toBe(200)
    expect((await request.get('/api/group')).status()).toBe(200)
    expect((await request.get('/api/admin/oauth/clients')).status()).toBe(200)
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
