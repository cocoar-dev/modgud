import assert from 'node:assert/strict'
import { test } from 'node:test'
import { registerHooks } from 'node:module'
import { createPinia, setActivePinia } from 'pinia'
import { adminNavigation, adminLandingPath, canSeeAdminItem } from '../src/router/admin-navigation.ts'

// Exercise the actual permission evaluator; isolate only unused network clients.
const hooks = registerHooks({
  resolve(specifier, context, next) {
    if (specifier === '@/composables/useHttpClient')
      return { url: 'data:text/javascript,export const useHttpClient=()=>({});', shortCircuit: true }
    if (specifier === '@/composables/useSignalR')
      return { url: 'data:text/javascript,export const useSignalR=()=>({});', shortCircuit: true }
    return next(specifier, context)
  },
})
const { useAuthStore } = await import('../src/stores/auth.store.ts')
hooks.deregister()

const features = { PositionTerminals: true, PageBuilder: true }
const items = adminNavigation.flatMap(section => section.items)
const destinations = [
  ['user:read', '/admin/users'],
  ['service-account:read', '/admin/service-accounts'],
  ['position:read', '/admin/positions'],
  ['permission-role:read', '/admin/roles'],
  ['authorization-group:read', '/admin/groups'],
  ['login-provider:read', '/admin/login-providers'],
  ['oauth-client:read', '/admin/oauth/clients'],
  ['oauth-scope:read', '/admin/oauth/scopes'],
  ['oauth-api:read', '/admin/oauth/apis'],
  ['invite-code:read', '/admin/invite-codes'],
  ['app:read', '/admin/apps'],
  ['realm:read', '/admin/realms'],
  ['realm-settings:read', '/admin/realm-settings'],
  ['auth-log:read', '/admin/logs'],
  ['audit-log:read', '/admin/logs'],
  ['platform-audit:read', '/admin/logs'],
  ['scheduled-job:read', '/admin/scheduled-jobs'],
  ['user:write', '/admin/change-requests'],
]
function auth(grants) {
  setActivePinia(createPinia())
  const store = useAuthStore()
  store.user = { Id: 'navigation-user', Permissions: [...grants] }
  return store
}

for (const [permission, path] of destinations) {
  test(`${permission} alone grants its own landing page and sidebar, without new grants`, () => {
    const store = auth([permission])
    assert.equal(adminLandingPath(store.hasPermission, features), path)
    assert.deepEqual(items.filter(item => canSeeAdminItem(item, store.hasPermission, features)).map(x => x.to), [path])
    assert.deepEqual(store.permissions, [permission])
  })
}

for (const [permission, path] of destinations.filter(([p]) => p.endsWith(':read') && !p.startsWith('realm:'))) {
  test(`${permission.split(':')[0]}:admin preserves the existing resource bypass`, () => {
    const store = auth([permission.replace(':read', ':admin')])
    assert.equal(adminLandingPath(store.hasPermission, features), path)
    assert.equal(store.hasPermission('realm:admin'), false)
    assert.equal(canSeeAdminItem(items.find(x => x.to === '/admin/realm-config'), store.hasPermission, features), false)
  })
}

test('realm:admin retains the users landing page and draft visibility', () => {
  const store = auth(['realm:admin'])
  assert.equal(adminLandingPath(store.hasPermission, features), '/admin/users')
  assert.equal(items.every(item => canSeeAdminItem(item, store.hasPermission, features)), true)
})

for (const grants of [[], ['service-account:write'], ['unknown:read'], ['session:read'], ['asset:read'], ['observability:read']]) {
  test(`no visible admin destination for ${grants.join(',') || 'no permissions'}`, () => {
    const store = auth(grants)
    assert.equal(adminLandingPath(store.hasPermission, features), undefined)
    assert.equal(items.some(item => canSeeAdminItem(item, store.hasPermission, features)), false)
  })
}

test('PositionTerminals is required even with position:admin; another visible area is still reachable', () => {
  const store = auth(['position:admin'])
  const off = { ...features, PositionTerminals: false }
  assert.equal(adminLandingPath(store.hasPermission, off), undefined)
  store.user.Permissions.push('scheduled-job:read')
  assert.equal(adminLandingPath(store.hasPermission, off), '/admin/scheduled-jobs')
  assert.equal(adminLandingPath(store.hasPermission, features), '/admin/positions')
})

test('landing order follows the sidebar, not grant order, and grants can be revoked', () => {
  const store = auth(['scheduled-job:read', 'service-account:read'])
  assert.equal(adminLandingPath(store.hasPermission, features), '/admin/service-accounts')
  store.user.Permissions = ['scheduled-job:read']
  assert.equal(adminLandingPath(store.hasPermission, features), '/admin/scheduled-jobs')
  store.user.Permissions = []
  assert.equal(adminLandingPath(store.hasPermission, features), undefined)
})

test('all ordered destinations are unique and permission-gated', () => {
  assert.equal(new Set(items.map(x => x.to)).size, 17)
  assert.equal(items.every(x => x.requirePermissions.length > 0 && x.to.startsWith('/admin/')), true)
})
