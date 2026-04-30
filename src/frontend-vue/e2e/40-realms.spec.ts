import { test, expect } from '@playwright/test'
import { apiLogin } from './helpers'

/**
 * §14 of the manual checklist — realm CRUD via the admin API. Multi-tenancy
 * is the security boundary that everything else is built on, so an
 * end-to-end "create + list + isolate" test against the production-mode
 * container catches regressions in:
 *
 *   - Marten `MasterTableTenancy` provisioning (a new DB gets created
 *     in the postgres cluster the auth container is wired to).
 *   - The realm-document seeding in the global schema.
 *   - The tenant DB getting its own OAuth scopes + Internal login provider
 *     seeded.
 *
 * The spec only asserts the **shape** the auth API exposes — verifying
 * cross-realm data isolation needs a separate Host-header switch which
 * the integration tests already cover.
 */

const ADMIN_USER = 'admin'
const ADMIN_PASSWORD = 'ABC12abc!'
const SUFFIX = Math.random().toString(36).slice(2, 8)

test.describe.configure({ mode: 'serial' })

test.describe('§14 Realms', () => {
  test.beforeEach(async ({ page }) => {
    await apiLogin(page, ADMIN_USER, ADMIN_PASSWORD)
  })

  test('system realm exists with CanManageTenants=true', async ({ page }) => {
    const list = await (await page.request.get('/api/admin/realms')).json() as { Items: { Slug: string; CanManageTenants: boolean; IsActive: boolean }[] }
    const sys = list.Items.find(r => r.Slug === 'system')
    expect(sys, 'system realm must exist').toBeDefined()
    expect(sys!.CanManageTenants).toBe(true)
    expect(sys!.IsActive).toBe(true)
  })

  test('POST /api/admin/realms provisions a new realm', async ({ page }) => {
    const slug = `acme-${SUFFIX}`
    const res = await page.request.post('/api/admin/realms', {
      data: {
        Slug: slug,
        DisplayName: `Acme ${SUFFIX}`,
        Description: 'phase-b realm provisioning test',
        Domains: [`${slug}.localhost`],
        CanManageTenants: false,
      },
    })
    if (!res.ok()) throw new Error(`create realm: ${res.status()} ${await res.text()}`)
    const body = await res.json()
    expect(body.Slug).toBe(slug)
    expect(body.IsActive).toBe(true)
    // NeedsSetup is calculated against the realm's own DB (does it have an
    // admin user yet?). The seed path provisions OAuth scopes + Internal
    // login provider but no admin, so the actual value depends on how the
    // RealmProvisioningService computes it; don't pin a specific bool here.
    expect(typeof body.NeedsSetup).toBe('boolean')
  })

  test('reserved + invalid realm slugs are rejected', async ({ page }) => {
    // RealmSlugRules enforces 3-63 chars, lowercase + digits + hyphens, no
    // reserved words. Spot-check the rejection paths.
    const cases = [
      { slug: 'system', why: 'reserved' },
      { slug: 'AB', why: 'too short / uppercase' },
      { slug: 'has spaces', why: 'invalid char' },
      { slug: '-leading-hyphen', why: 'invalid leading char' },
    ]
    for (const c of cases) {
      const res = await page.request.post('/api/admin/realms', {
        data: { Slug: c.slug, DisplayName: 'Should Fail', Domains: [`${c.slug}.test`] },
      })
      expect(res.status(), `slug "${c.slug}" (${c.why}) should be rejected`).toBe(400)
    }
  })
})
