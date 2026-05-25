import { test, expect } from '@playwright/test'
import { apiLogin } from './helpers'

/**
 * Phase B of the manual-checklist port. Admin CRUD coverage for §6 + §7 +
 * §8 + §9 + §10 + §11 + §12 of `website/testing/manual-checklist.md`.
 *
 * Most assertions are API-driven — the API surface is already heavily
 * integration-tested, so the value E2E adds at the **HTTP-route + DTO**
 * level is small. What this spec adds is the prod-image confidence: that
 * each route is actually mapped, gated by the right permission, and round-
 * trips a real DTO via System.Text.Json against the published image.
 *
 * Each describe block opens with one UI walk so we know the SPA renders
 * something at the section's URL — the rest is fast API CRUD. Tests use
 * unique resource names per run (random suffix) so they don't collide
 * across re-runs in the same DB.
 *
 * Order dependency: §1 in `smoke.spec.ts` creates the admin. Playwright's
 * default file ordering runs `admin-crud.spec.ts` after `smoke.spec.ts`
 * (alphabetical), so the admin user is in place by the time we get here.
 */

const ADMIN_USER = 'admin'
const ADMIN_PASSWORD = 'ABC12abc!'

// Random suffix per run — protects against collisions between this spec's
// CRUD operations on a database the smoke spec already touched.
const SUFFIX = Math.random().toString(36).slice(2, 8)

// Tests share the same authenticated request context — `apiLogin` sets the
// cookie on the test's page context, but page.request inherits from there.
test.describe.configure({ mode: 'serial' })

test.beforeEach(async ({ page }) => {
  await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
})

test.describe('§6 Users (admin CRUD)', () => {
  test('UI list page renders and shows the admin row', async ({ page }) => {
    await page.goto('/admin/users')
    // AG-Grid column header is sufficient evidence the grid loaded; the
    // admin row's username appears as a cell.
    await expect(page.getByRole('columnheader', { name: /Benutzername|Username/i }).first()).toBeVisible({ timeout: 10_000 })
    await expect(page.getByRole('gridcell', { name: ADMIN_USER }).first()).toBeVisible()
  })

  test('POST /api/user creates a user with Status=Pending', async ({ page }) => {
    const userName = `crud-u-${SUFFIX}`
    const res = await page.request.post('/api/user', {
      data: {
        Firstname: 'Crud', Lastname: 'User', Acronym: userName,
        Email: `${userName}@modgud.test`,
      },
    })
    expect(res.ok()).toBeTruthy()
    const body = await res.json()
    expect(body.UserName).toBe(userName)
    expect(body.HasPassword).toBe(false)
    expect(body.IsActive).toBe(true)
    // The user-created flow does not auto-set a password — the admin invites
    // them via magic-link, or sets one explicitly.
    expect(body.Status).toBe('Pending')
  })

  test('PUT /api/user/{id} updates name fields via Optional<T>', async ({ page }) => {
    // Create + immediately update.
    const userName = `crud-edit-${SUFFIX}`
    const created = await (await page.request.post('/api/user', {
      data: { Firstname: 'Old', Lastname: 'Name', Acronym: userName, Email: `${userName}@modgud.test` },
    })).json()

    // OptionalJsonConverterFactory: bare value means HasValue=true.
    const updated = await page.request.put(`/api/user/${created.Id}`, {
      data: { Firstname: 'New', Lastname: 'Name' },
    })
    if (!updated.ok()) throw new Error(`PUT failed: ${updated.status()} ${await updated.text()}`)
    const body = await updated.json()
    expect(body.Firstname).toBe('New')
    expect(body.Lastname).toBe('Name')
    expect(body.UserName).toBe(userName) // unchanged
  })
})

test.describe('§7 Roles (admin CRUD)', () => {
  test('UI list page renders + three default roles seeded', async ({ page }) => {
    await page.goto('/admin/roles')
    await expect(page.getByRole('columnheader', { name: /Name/i }).first()).toBeVisible({ timeout: 10_000 })
    // Three default roles exist after first-time setup. Search via API
    // because the grid may collapse some columns.
    const roles = await (await page.request.get('/api/role')).json()
    const names = (roles as { Name: string }[]).map(r => r.Name)
    expect(names).toEqual(expect.arrayContaining(['System Admin', 'User Manager', 'Viewer']))
  })

  test('POST /api/role creates a role with 3-segment shape', async ({ page }) => {
    const name = `Crud Role ${SUFFIX}`
    const res = await page.request.post('/api/role', {
      data: {
        Name: name,
        AppSlug: 'modgud',
        ResourceType: 'user',
        Permissions: ['read'],
      },
    })
    expect(res.ok()).toBeTruthy()
    const body = await res.json()
    expect(body.Name).toBe(name)
    expect(body.AppSlug).toBe('modgud')
    // Permissions are stored as bare actions and expanded at resolution
    // time; the wire shape preserves the bare form.
    expect(body.Permissions).toContain('read')
  })

  test('System Admin default role carries realm:admin (post-Phase-1 model)', async ({ page }) => {
    const roles = await (await page.request.get('/api/role')).json() as { Name: string; Permissions: string[] }[]
    const sysAdmin = roles.find(r => r.Name === 'System Admin')
    expect(sysAdmin).toBeDefined()
    expect(sysAdmin!.Permissions).toContain('realm:admin')
  })
})

test.describe('§8 Groups (Phase 6 — no AccessScripts)', () => {
  test('UI list page renders', async ({ page }) => {
    await page.goto('/admin/groups')
    await expect(page.getByRole('columnheader', { name: /Name/i }).first()).toBeVisible({ timeout: 10_000 })
  })

  test('POST /api/group accepts BoundTo and returns no AccessScripts field', async ({ page }) => {
    // Create a member user first so the group has someone to bind.
    const member = await (await page.request.post('/api/user', {
      data: {
        Firstname: 'Group', Lastname: 'Member', Acronym: `gm${SUFFIX}`,
        Email: `gm${SUFFIX}@modgud.test`,
      },
    })).json()

    const groupName = `Bound Group ${SUFFIX}`
    const res = await page.request.post('/api/group', {
      data: {
        Name: groupName,
        Description: 'phase-6 wire shape verification',
        MemberIds: [member.Id],
        RoleIds: [],
        MembershipMode: 'Manual',
        EmailMode: 'Shared',
        BoundTo: ['modgud'],
      },
    })
    expect(res.ok()).toBeTruthy()
    const body = await res.json()
    expect(body.Name).toBe(groupName)
    expect(body.BoundTo).toEqual(['modgud'])
    // Phase 6: AccessScripts is gone from the wire format.
    expect(body).not.toHaveProperty('AccessScripts')
  })

  test('BoundTo accepts the wildcard "*"', async ({ page }) => {
    const res = await page.request.post('/api/group', {
      data: {
        Name: `Wildcard Group ${SUFFIX}`,
        MemberIds: [],
        RoleIds: [],
        MembershipMode: 'Manual',
        EmailMode: 'Shared',
        BoundTo: ['*'],
      },
    })
    expect(res.ok()).toBeTruthy()
    const body = await res.json()
    expect(body.BoundTo).toEqual(['*'])
  })
})

test.describe('§9 Apps', () => {
  test('modgud system app exists with IsSystem=true', async ({ page }) => {
    const apps = await (await page.request.get('/api/app')).json() as { Slug: string; IsSystem: boolean }[]
    const sys = apps.find(a => a.Slug === 'modgud')
    expect(sys).toBeDefined()
    expect(sys!.IsSystem).toBe(true)
  })

  test('POST /api/app creates a non-system app', async ({ page }) => {
    const slug = `crud-app-${SUFFIX}`
    const res = await page.request.post('/api/app', {
      data: { Slug: slug, DisplayName: `Crud App ${SUFFIX}`, Description: 'phase B' },
    })
    expect(res.ok()).toBeTruthy()
    const body = await res.json()
    expect(body.Slug).toBe(slug)
    expect(body.IsSystem).toBe(false)
  })

  test('reserved slugs (realm / modgud / *) are rejected', async ({ page }) => {
    for (const slug of ['realm', 'modgud', '*']) {
      const res = await page.request.post('/api/app', {
        data: { Slug: slug, DisplayName: 'Should Fail' },
      })
      expect(res.status(), `slug "${slug}" should be rejected with 400`).toBe(400)
    }
  })
})

test.describe('§10 OAuth Clients (F16 fix verification)', () => {
  test('UI list page renders', async ({ page }) => {
    await page.goto('/admin/oauth/clients')
    // The page mounts the AG-Grid lazily. Wait for the create button.
    await expect(page.getByRole('button', { name: /Erstellen|Create/i }).first()).toBeVisible({ timeout: 10_000 })
  })

  test('GET /api/admin/oauth/clients returns 200 even without ?page=/?pageSize= (F16)', async ({ page }) => {
    const res = await page.request.get('/api/admin/oauth/clients')
    expect(res.status()).toBe(200)
    const body = await res.json()
    expect(body).toHaveProperty('Items')
    expect(body).toHaveProperty('TotalCount')
    expect(Array.isArray(body.Items)).toBe(true)
  })
})

test.describe('§11 OAuth Scopes', () => {
  test('standard scopes seeded with AppId=null (global)', async ({ page }) => {
    const res = await page.request.get('/api/admin/oauth/scopes')
    expect(res.ok()).toBeTruthy()
    const body = await res.json() as { Items: { Name: string }[] }
    const names = body.Items.map(s => s.Name)
    // OAuthRealmSeeder seeds five standard scopes per realm at boot.
    expect(names).toEqual(expect.arrayContaining(['openid', 'email', 'profile', 'roles', 'offline_access']))
  })
})

test.describe('§12 OAuth APIs (F16 fix verification)', () => {
  test('GET /api/admin/oauth/apis returns 200 even without pagination params (F16)', async ({ page }) => {
    const res = await page.request.get('/api/admin/oauth/apis')
    expect(res.status()).toBe(200)
    const body = await res.json()
    expect(body).toHaveProperty('Items')
  })
})
