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
  /**
   * English fallback shown when `label` isn't in the loaded locale file.
   * The sidebar resolves labels with `t(label, {}, fallback)`; without this
   * the fallback would be the raw key, so any item whose key is missing from
   * the active locale must set an English default here.
   */
  labelEn?: string
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
    label: t(def.label, {}, def.labelEn ?? def.label),
    icon: def.icon,
    to: def.to,
    visible: canSee(def),
  }
}

const sections = computed<SectionDef[]>(() => [
  {
    key: 'authorization',
    heading: t('admin.section.authorization', {}, 'Authorization'),
    items: [
      { label: 'nav.users', labelEn: 'Users', icon: 'users', to: '/admin/users', requirePermissions: ['user:read'] },
      { label: 'admin.serviceAccounts.title', labelEn: 'Service Accounts', icon: 'cpu', to: '/admin/service-accounts', requirePermissions: ['service-account:read'] },
      { label: 'nav.roles', labelEn: 'Roles', icon: 'shield', to: '/admin/roles', requirePermissions: ['permission-role:read'] },
      { label: 'nav.groups', labelEn: 'Groups', icon: 'users-round', to: '/admin/groups', requirePermissions: ['authorization-group:read'] },
    ],
  },
  {
    key: 'oauth',
    heading: t('admin.section.oauth', {}, 'OAuth & Federation'),
    items: [
      { label: 'admin.loginProviders.title', labelEn: 'Login Providers', icon: 'log-in', to: '/admin/login-providers', requirePermissions: ['login-provider:read'] },
      { label: 'admin.oauthClients.title', labelEn: 'OAuth Clients', icon: 'app-window', to: '/admin/oauth/clients', requirePermissions: ['oauth-client:read'] },
      { label: 'admin.oauthScopes.title', labelEn: 'OAuth Scopes', icon: 'tags', to: '/admin/oauth/scopes', requirePermissions: ['oauth-scope:read'] },
      { label: 'admin.oauthApis.title', labelEn: 'OAuth APIs', icon: 'server', to: '/admin/oauth/apis', requirePermissions: ['oauth-api:read'] },
      { label: 'admin.inviteCodes.title', labelEn: 'Invite Codes', icon: 'ticket', to: '/admin/invite-codes', requirePermissions: ['invite-code:read'] },
    ],
  },
  {
    key: 'system',
    heading: t('admin.section.system', {}, 'System'),
    items: [
      { label: 'admin.apps.title', labelEn: 'Applications', icon: 'layout-grid', to: '/admin/apps', requirePermissions: ['app:read'] },
      { label: 'admin.realms.title', labelEn: 'Realms', icon: 'globe', to: '/admin/realms', requirePermissions: ['realm:read'] },
      { label: 'admin.realmSettings.title', labelEn: 'Realm Settings', icon: 'sliders-horizontal', to: '/admin/realm-settings', requirePermissions: ['realm-settings:read'] },
      { label: 'admin.logs.title', labelEn: 'Logs', icon: 'scroll-text', to: '/admin/logs', requirePermissions: ['auth-log:read', 'audit-log:read', 'platform-audit:read'] },
      { label: 'admin.scheduledJobs.title', labelEn: 'Scheduled Jobs', icon: 'clock', to: '/admin/scheduled-jobs', requirePermissions: ['scheduled-job:read'] },
      { label: 'admin.changeRequests.title', labelEn: 'Change Requests', icon: 'inbox', to: '/admin/change-requests', requirePermissions: ['user:write'] },
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
