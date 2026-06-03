<script setup lang="ts">
import { computed } from 'vue'
import { RouterView } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import SubNavLayoutGrouped from '@/layouts/SubNavLayoutGrouped.vue'
import type { SubNavGroup, SubNavItem } from '@/layouts/sub-nav-types'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'

const { t } = useI18n()
const authStore = useAuthStore()
const appConfig = useAppConfigStore()

interface NavItemDef {
  label: string
  icon: string
  to: string
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

interface SectionDef {
  key: 'authorization' | 'oauth' | 'system'
  heading: string
  items: NavItemDef[]
}

// Per-resource visibility — `authStore.hasPermission` already bypasses on
// realm:admin and the app/resource admin shortcuts. Plus optional
// operator-feature gate; both must pass.
function canSee(item: NavItemDef): boolean {
  if (item.requireFeature && !appConfig.config.Features[item.requireFeature]) return false
  return item.requirePermissions.some((p) => authStore.hasPermission(p))
}

function toNavItem(def: NavItemDef): SubNavItem {
  return {
    label: t(def.label, {}, def.label),
    icon: def.icon,
    to: def.to,
    visible: canSee(def),
  }
}

const sections = computed<SectionDef[]>(() => [
  {
    key: 'authorization',
    heading: t('admin.section.authorization', {}, 'Autorisierung'),
    items: [
      { label: 'nav.users', icon: 'users', to: '/admin/users', requirePermissions: ['user:read'] },
      { label: 'admin.serviceAccounts.title', icon: 'cpu', to: '/admin/service-accounts', requirePermissions: ['service-account:read'] },
      { label: 'nav.roles', icon: 'shield', to: '/admin/roles', requirePermissions: ['permission-role:read'] },
      { label: 'nav.groups', icon: 'users-round', to: '/admin/groups', requirePermissions: ['authorization-group:read'] },
    ],
  },
  {
    key: 'oauth',
    heading: t('admin.section.oauth', {}, 'OAuth & Federation'),
    items: [
      { label: 'admin.loginProviders.title', icon: 'log-in', to: '/admin/login-providers', requirePermissions: ['login-provider:read'] },
      { label: 'admin.oauthClients.title', icon: 'app-window', to: '/admin/oauth/clients', requirePermissions: ['oauth-client:read'] },
      { label: 'admin.oauthScopes.title', icon: 'tags', to: '/admin/oauth/scopes', requirePermissions: ['oauth-scope:read'] },
      { label: 'admin.oauthApis.title', icon: 'server', to: '/admin/oauth/apis', requirePermissions: ['oauth-api:read'] },
    ],
  },
  {
    key: 'system',
    heading: t('admin.section.system', {}, 'System'),
    items: [
      { label: 'admin.apps.title', icon: 'layout-grid', to: '/admin/apps', requirePermissions: ['app:read'] },
      { label: 'admin.realms.title', icon: 'globe', to: '/admin/realms', requirePermissions: ['realm:read'] },
      { label: 'admin.realmSettings.title', icon: 'sliders-horizontal', to: '/admin/realm-settings', requirePermissions: ['realm-settings:read'] },
      { label: 'admin.securityLog.title', icon: 'shield-alert', to: '/admin/auth-log', requirePermissions: ['auth-log:read'] },
      { label: 'admin.auditLog.title', icon: 'history', to: '/admin/audit', requirePermissions: ['audit-log:read'] },
      { label: 'admin.scheduledJobs.title', icon: 'clock', to: '/admin/scheduled-jobs', requirePermissions: ['scheduled-job:read'] },
      { label: 'admin.changeRequests.title', icon: 'inbox', to: '/admin/change-requests', requirePermissions: ['user:write'] },
    ],
  },
])

const adminGroups = computed<SubNavGroup[]>(() =>
  sections.value.map((section) => ({
    title: section.heading,
    items: section.items.map(toNavItem),
  })),
)
</script>

<template>
  <SubNavLayoutGrouped :groups="adminGroups">
    <RouterView class="flex-1 min-h-0" />
  </SubNavLayoutGrouped>
</template>
