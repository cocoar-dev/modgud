import type { FeatureFlags } from '../stores/appconfig.store'

export interface AdminNavItem {
  label: string
  labelEn: string
  icon: string
  to: string
  /** Any matching permission; the caller supplies the existing realm/resource bypasses. */
  requirePermissions: readonly string[]
  requireFeature?: keyof FeatureFlags
}

interface AdminNavSection {
  key: string
  heading: string
  headingEn: string
  items: readonly AdminNavItem[]
}

// One ordered definition for the sidebar, shell entry and /admin landing page.
// These are navigation gates only; the server remains the authorization boundary.
export const adminNavigation: readonly AdminNavSection[] = [
  {
    key: 'authorization',
    heading: 'admin.section.authorization',
    headingEn: 'Authorization',
    items: [
      { label: 'nav.users', labelEn: 'Users', icon: 'users', to: '/admin/users', requirePermissions: ['user:read'] },
      { label: 'admin.serviceAccounts.title', labelEn: 'Service Accounts', icon: 'cpu', to: '/admin/service-accounts', requirePermissions: ['service-account:read'] },
      { label: 'admin.positions.title', labelEn: 'Positions', icon: 'briefcase', to: '/admin/positions', requirePermissions: ['position:read'], requireFeature: 'PositionTerminals' },
      { label: 'nav.roles', labelEn: 'Roles', icon: 'shield', to: '/admin/roles', requirePermissions: ['permission-role:read'] },
      { label: 'nav.groups', labelEn: 'Groups', icon: 'users-round', to: '/admin/groups', requirePermissions: ['authorization-group:read'] },
    ],
  },
  {
    key: 'oauth',
    heading: 'admin.section.oauth',
    headingEn: 'OAuth & Federation',
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
    heading: 'admin.section.system',
    headingEn: 'System',
    items: [
      { label: 'admin.apps.title', labelEn: 'Applications', icon: 'layout-grid', to: '/admin/apps', requirePermissions: ['app:read'] },
      { label: 'admin.realms.title', labelEn: 'Realms', icon: 'globe', to: '/admin/realms', requirePermissions: ['realm:read'] },
      { label: 'admin.realmSettings.title', labelEn: 'Realm Settings', icon: 'sliders-horizontal', to: '/admin/realm-settings', requirePermissions: ['realm-settings:read'] },
      // Draft endpoints require realm:admin, not a resource-level permission.
      { label: 'admin.realmConfig.title', labelEn: 'Configuration Drafts', icon: 'file-json', to: '/admin/realm-config', requirePermissions: ['realm:admin'] },
      { label: 'admin.logs.title', labelEn: 'Logs', icon: 'scroll-text', to: '/admin/logs', requirePermissions: ['auth-log:read', 'audit-log:read', 'platform-audit:read'] },
      { label: 'admin.scheduledJobs.title', labelEn: 'Scheduled Jobs', icon: 'clock', to: '/admin/scheduled-jobs', requirePermissions: ['scheduled-job:read'] },
      { label: 'admin.changeRequests.title', labelEn: 'Change Requests', icon: 'inbox', to: '/admin/change-requests', requirePermissions: ['user:write'] },
    ],
  },
]

export function canSeeAdminItem(
  item: AdminNavItem,
  hasPermission: (permission: string) => boolean,
  features: FeatureFlags,
): boolean {
  return (!item.requireFeature || features[item.requireFeature])
    && item.requirePermissions.some(hasPermission)
}

export function adminLandingPath(
  hasPermission: (permission: string) => boolean,
  features: FeatureFlags,
): string | undefined {
  for (const section of adminNavigation) {
    const item = section.items.find(item => canSeeAdminItem(item, hasPermission, features))
    if (item) return item.to
  }
}
