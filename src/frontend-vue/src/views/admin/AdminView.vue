<script setup lang="ts">
import { computed } from 'vue'
import { useRouter, useRoute, RouterView } from 'vue-router'
import { CoarMenu, CoarMenuItem } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'

const { t } = useI18n()
const router = useRouter()
const route = useRoute()
const authStore = useAuthStore()
const appConfig = useAppConfigStore()

interface NavItem {
  /** Section heading the item belongs to. Used to hide empty sections. */
  section: 'authorization' | 'oauth' | 'customization' | 'system'
  label: string
  icon: string
  path: string
  /**
   * Resource permissions that grant visibility. Matches if the user holds
   * any of these. `app:admin` is a global bypass and applied implicitly by
   * `authStore.hasPermission`.
   */
  requirePermissions: string[]
  /**
   * Optional gate that hides the item when the named operator-level
   * feature flag is off. Independent from permissions — both must pass.
   */
  requireFeature?: 'PageBuilder'
}

// Order matters — drives both rendering order and the section-grouping below.
const allNavItems: NavItem[] = [
  // Authorization
  { section: 'authorization', label: 'nav.users', icon: 'users', path: '/admin/users', requirePermissions: ['user:read'] },
  { section: 'authorization', label: 'admin.serviceAccounts.title', icon: 'cpu', path: '/admin/service-accounts', requirePermissions: ['service-account:read'] },
  { section: 'authorization', label: 'nav.roles', icon: 'shield', path: '/admin/roles', requirePermissions: ['permission-role:read'] },
  { section: 'authorization', label: 'nav.groups', icon: 'users-round', path: '/admin/groups', requirePermissions: ['authorization-group:read'] },
  // OAuth & Federation
  { section: 'oauth', label: 'admin.loginProviders.title', icon: 'log-in', path: '/admin/login-providers', requirePermissions: ['login-provider:read'] },
  { section: 'oauth', label: 'admin.oauthClients.title', icon: 'app-window', path: '/admin/oauth/clients', requirePermissions: ['oauth-client:read'] },
  { section: 'oauth', label: 'admin.oauthScopes.title', icon: 'tags', path: '/admin/oauth/scopes', requirePermissions: ['oauth-scope:read'] },
  { section: 'oauth', label: 'admin.oauthApis.title', icon: 'server', path: '/admin/oauth/apis', requirePermissions: ['oauth-api:read'] },
  // Customization (SPA-shell branding + per-page form composition)
  { section: 'customization', label: 'admin.customization.branding.title', icon: 'palette', path: '/admin/customization/branding', requirePermissions: ['realm-settings:read'] },
  { section: 'customization', label: 'admin.customization.pages.title', icon: 'layout-template', path: '/admin/customization/pages', requirePermissions: ['realm-settings:read'], requireFeature: 'PageBuilder' },
  { section: 'customization', label: 'admin.assets.title', icon: 'image', path: '/admin/customization/assets', requirePermissions: ['asset:read'] },
  // System
  { section: 'system', label: 'admin.apps.title', icon: 'layout-grid', path: '/admin/apps', requirePermissions: ['app:read'] },
  { section: 'system', label: 'admin.realms.title', icon: 'globe', path: '/admin/realms', requirePermissions: ['realm:read'] },
  { section: 'system', label: 'admin.realmSettings.title', icon: 'sliders-horizontal', path: '/admin/realm-settings', requirePermissions: ['realm-settings:read'] },
  { section: 'system', label: 'admin.authLog.title', icon: 'scroll-text', path: '/admin/auth-log', requirePermissions: ['auth-log:read'] },
  { section: 'system', label: 'admin.observability.title', icon: 'activity', path: '/admin/observability', requirePermissions: ['observability:read'] },
  { section: 'system', label: 'admin.changeRequests.title', icon: 'inbox', path: '/admin/change-requests', requirePermissions: ['user:write'] },
  { section: 'system', label: 'nav.settings', icon: 'settings', path: '/admin/settings', requirePermissions: ['realm:admin'] },
]

// Per-resource visibility — `authStore.hasPermission` already bypasses on
// realm:admin and the app/resource admin shortcuts. Plus optional
// operator-feature gate; both must pass.
function canSee(item: NavItem): boolean {
  if (item.requireFeature && !appConfig.config.Features[item.requireFeature]) return false
  return item.requirePermissions.some((p) => authStore.hasPermission(p))
}

const visibleItems = computed(() => allNavItems.filter(canSee))

interface Section {
  key: NavItem['section']
  heading: string
  items: NavItem[]
}

const sections = computed<Section[]>(() => {
  const grouped: Record<NavItem['section'], NavItem[]> = {
    authorization: [],
    oauth: [],
    customization: [],
    system: [],
  }
  for (const item of visibleItems.value) {
    grouped[item.section].push(item)
  }
  // Render-order: same as `allNavItems`, but skip empty sections.
  const all: Section[] = [
    { key: 'authorization', heading: t('admin.section.authorization', {}, 'Autorisierung'), items: grouped.authorization },
    { key: 'oauth', heading: t('admin.section.oauth', {}, 'OAuth & Federation'), items: grouped.oauth },
    { key: 'customization', heading: t('admin.section.customization', {}, 'Anpassung'), items: grouped.customization },
    { key: 'system', heading: t('admin.section.system', {}, 'System'), items: grouped.system },
  ]
  return all.filter((s) => s.items.length > 0)
})

function isActive(item: NavItem): boolean {
  return route.path.startsWith(item.path)
}
</script>

<template>
  <div class="flex min-h-0 flex-1">
    <!-- Left: Navigation menu -->
    <div class="sub-nav flex-shrink-0 p-4 flex flex-col min-h-0 overflow-y-auto">
      <template v-for="section in sections" :key="section.key">
        <div class="section-heading">{{ section.heading }}</div>
        <CoarMenu class="mb-3">
          <CoarMenuItem
            v-for="item in section.items"
            :key="item.path"
            :icon="item.icon"
            :label="t(item.label, {}, item.label)"
            :class="{ 'admin-menu-item--active': isActive(item) }"
            @clicked="router.push(item.path)"
          />
        </CoarMenu>
      </template>
    </div>

    <!-- Right: Content -->
    <div class="flex-1 flex justify-center min-w-0">
      <div class="flex w-11/12">
        <RouterView class="flex-1 min-h-0" />
      </div>
    </div>
  </div>
</template>

<style scoped>
.sub-nav {
  width: 14rem;
  height: 100%;
  --coar-background-neutral-primary: var(--coar-background-neutral-secondary, #f7f7f7);
}

.section-heading {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: #525e76;
  padding: 0 0.5rem 0.25rem;
  margin-top: 0.5rem;
}
.section-heading:first-of-type {
  margin-top: 0;
}

.admin-menu-item--active {
  background: var(--coar-menu-item-background-active, #eff6ff);
  color: var(--coar-menu-item-text-active, #1d4ed8);
  font-weight: 500;
  border-radius: 6px;
}
</style>
