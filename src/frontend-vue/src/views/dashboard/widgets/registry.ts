import type { WidgetDefinition, WidgetOptionDefinition, WidgetPlacement } from '../layout'
import AccountSecurityWidget from './AccountSecurityWidget.vue'
import CountsWidget from './CountsWidget.vue'
import LoginMethodsWidget from './LoginMethodsWidget.vue'
import LoginProvidersWidget from './LoginProvidersWidget.vue'
import LoginsChartWidget from './LoginsChartWidget.vue'
import MyAppsWidget from './MyAppsWidget.vue'
import MyPasskeysWidget from './MyPasskeysWidget.vue'
import MySessionsWidget from './MySessionsWidget.vue'
import RecentActivityWidget from './RecentActivityWidget.vue'
import SecurityEventsWidget from './SecurityEventsWidget.vue'
import StatKpiWidget from './StatKpiWidget.vue'

/** "How many rows" — the one setting the list widgets share. */
function rowsOption(choices: number[], fallback: number): WidgetOptionDefinition {
  return {
    key: 'rows',
    labelKey: 'dashboard.options.rows', labelEn: 'Rows',
    kind: 'single',
    choices: choices.map(n => ({ value: String(n), labelKey: `dashboard.options.rowCount.${n}`, labelEn: String(n) })),
    defaults: [String(fallback)],
  }
}

/**
 * Every widget the dashboard can show. Ids, option keys and choice values are
 * persisted in stored layouts — never rename one; a removed one simply drops out
 * of the layouts that name it. `requirePermissions` mirrors the backend strings.
 */
export const WIDGETS: WidgetDefinition[] = [
  // ── Personal — every signed-in user ──────────────────────────────────
  {
    id: 'account-security',
    titleKey: 'dashboard.security.title', titleEn: 'Account security', icon: 'shield-check',
    requirePermissions: [],
    sizes: ['m', 'l', 'xl', 'full'], defaultSize: 'm',
    component: AccountSecurityWidget,
  },
  {
    id: 'my-sessions',
    titleKey: 'dashboard.sessions.title', titleEn: 'Active sessions', icon: 'monitor',
    requirePermissions: [],
    sizes: ['m', 'l', 'xl', 'full'], defaultSize: 'm',
    component: MySessionsWidget,
    options: [rowsOption([3, 5, 10], 3)],
  },
  {
    id: 'my-apps',
    titleKey: 'dashboard.myApps.title', titleEn: 'My apps', icon: 'app-window',
    requirePermissions: [],
    sizes: ['m', 'l', 'xl', 'full'], defaultSize: 'm',
    component: MyAppsWidget,
    options: [rowsOption([3, 5, 10], 5)],
  },
  {
    id: 'kpi-my-passkeys',
    titleKey: 'dashboard.kpi.myPasskeys', titleEn: 'My passkeys', icon: 'fingerprint',
    requirePermissions: [],
    sizes: ['xs', 's', 'm'], defaultSize: 's',
    component: MyPasskeysWidget,
  },

  // ── Realm operations — each gated on what backs its data ─────────────
  {
    id: 'kpi-active-sessions',
    titleKey: 'dashboard.kpi.realmSessions', titleEn: 'Active sessions in the realm', icon: 'monitor',
    requirePermissions: ['session:read'],
    sizes: ['xs', 's', 'm'], defaultSize: 's',
    component: StatKpiWidget, props: { kind: 'activeSessions' },
  },
  {
    id: 'kpi-failed-logins',
    titleKey: 'dashboard.kpi.failedLast24h', titleEn: 'Failed sign-ins, 24 h', icon: 'shield-alert',
    requirePermissions: ['audit-log:read'],
    sizes: ['xs', 's', 'm'], defaultSize: 's',
    component: StatKpiWidget, props: { kind: 'failedLogins' },
  },
  {
    id: 'kpi-security-attention',
    titleKey: 'dashboard.kpi.securityAttention', titleEn: 'Security events, 24 h', icon: 'triangle-alert',
    requirePermissions: ['auth-log:read'],
    sizes: ['xs', 's', 'm'], defaultSize: 's',
    component: StatKpiWidget, props: { kind: 'securityAttention' },
  },
  {
    id: 'kpi-pending-requests',
    titleKey: 'dashboard.kpi.pendingChangeRequests', titleEn: 'Open requests', icon: 'inbox',
    requirePermissions: ['user:write'],
    sizes: ['xs', 's', 'm'], defaultSize: 's',
    component: StatKpiWidget, props: { kind: 'pendingRequests' },
  },
  {
    id: 'logins-chart',
    titleKey: 'dashboard.logins.title', titleEn: 'Sign-ins', icon: 'activity',
    requirePermissions: ['audit-log:read'],
    sizes: ['l', 'xl', 'full'], defaultSize: 'xl',
    component: LoginsChartWidget,
  },
  {
    id: 'login-methods',
    titleKey: 'dashboard.loginMethods.title', titleEn: 'Sign-in methods', icon: 'key-round',
    requirePermissions: ['audit-log:read'],
    sizes: ['m', 'l'], defaultSize: 'm',
    component: LoginMethodsWidget,
  },
  {
    id: 'directory',
    titleKey: 'dashboard.directory.title', titleEn: 'Directory', icon: 'users',
    requirePermissions: ['user:read', 'service-account:read', 'position:read', 'authorization-group:read', 'permission-role:read'],
    sizes: ['m', 'l', 'xl', 'full'], defaultSize: 'l',
    component: CountsWidget, props: { group: 'directory' },
    // Choice values are the count keys of the statistics endpoint.
    options: [{
      key: 'show',
      labelKey: 'dashboard.options.show', labelEn: 'Show',
      kind: 'multi',
      choices: [
        { value: 'Users', labelKey: 'nav.users', labelEn: 'Users', requirePermissions: ['user:read'] },
        { value: 'ServiceAccounts', labelKey: 'admin.serviceAccounts.title', labelEn: 'Service Accounts', requirePermissions: ['service-account:read'] },
        { value: 'Positions', labelKey: 'admin.positions.title', labelEn: 'Positions', requirePermissions: ['position:read'], requireFeature: 'PositionTerminals' },
        { value: 'Groups', labelKey: 'nav.groups', labelEn: 'Groups', requirePermissions: ['authorization-group:read'] },
        { value: 'Roles', labelKey: 'nav.roles', labelEn: 'Roles', requirePermissions: ['permission-role:read'] },
      ],
      defaults: ['Users', 'ServiceAccounts', 'Positions', 'Groups', 'Roles'],
    }],
  },
  {
    id: 'applications',
    titleKey: 'dashboard.applications.title', titleEn: 'Applications & sign-in', icon: 'layout-grid',
    requirePermissions: ['app:read', 'oauth-client:read', 'oauth-api:read', 'oauth-scope:read', 'login-provider:read'],
    sizes: ['m', 'l', 'xl', 'full'], defaultSize: 'l',
    component: CountsWidget, props: { group: 'applications' },
    options: [{
      key: 'show',
      labelKey: 'dashboard.options.show', labelEn: 'Show',
      kind: 'multi',
      choices: [
        { value: 'Apps', labelKey: 'admin.apps.title', labelEn: 'Applications', requirePermissions: ['app:read'] },
        { value: 'OAuthClients', labelKey: 'admin.oauthClients.title', labelEn: 'OAuth Clients', requirePermissions: ['oauth-client:read'] },
        { value: 'OAuthApis', labelKey: 'admin.oauthApis.title', labelEn: 'OAuth APIs', requirePermissions: ['oauth-api:read'] },
        { value: 'OAuthScopes', labelKey: 'admin.oauthScopes.title', labelEn: 'OAuth Scopes', requirePermissions: ['oauth-scope:read'] },
        { value: 'LoginProviders', labelKey: 'admin.loginProviders.title', labelEn: 'Login Providers', requirePermissions: ['login-provider:read'] },
      ],
      defaults: ['Apps', 'OAuthClients', 'LoginProviders'],
    }],
  },
  {
    id: 'security-events',
    titleKey: 'dashboard.securityEvents.title', titleEn: 'Security events', icon: 'shield-alert',
    requirePermissions: ['auth-log:read'],
    sizes: ['m', 'l'], defaultSize: 'm',
    component: SecurityEventsWidget,
  },
  {
    id: 'recent-activity',
    titleKey: 'dashboard.systemActivity.title', titleEn: 'Recent security events', icon: 'scroll-text',
    requirePermissions: ['auth-log:read'],
    sizes: ['l', 'xl', 'full'], defaultSize: 'xl',
    component: RecentActivityWidget,
    options: [rowsOption([5, 8, 15], 8)],
  },
  {
    id: 'login-providers',
    titleKey: 'dashboard.loginProviderStatus.title', titleEn: 'Login providers', icon: 'log-in',
    requirePermissions: ['login-provider:read'],
    sizes: ['m', 'l', 'xl'], defaultSize: 'l',
    component: LoginProvidersWidget,
  },
]

export const WIDGET_CATALOG = new Map(WIDGETS.map(w => [w.id, w]))

/**
 * What a realm shows before anyone arranged anything — the last of the three
 * layers (own layout → realm default → this). Rows on the 12-column grid add up
 * for a viewer who sees everything; with fewer permissions the rest closes ranks.
 */
export const BUILT_IN_LAYOUT: WidgetPlacement[] = [
  { Widget: 'account-security', Size: 'm' },
  { Widget: 'my-sessions', Size: 'm' },
  { Widget: 'my-apps', Size: 'm' },

  { Widget: 'kpi-active-sessions', Size: 's' },
  { Widget: 'kpi-failed-logins', Size: 's' },
  { Widget: 'kpi-security-attention', Size: 's' },
  { Widget: 'kpi-pending-requests', Size: 's' },

  { Widget: 'logins-chart', Size: 'xl' },
  { Widget: 'login-methods', Size: 'm' },

  { Widget: 'directory', Size: 'l' },
  { Widget: 'applications', Size: 'l' },

  { Widget: 'security-events', Size: 'm' },
  { Widget: 'recent-activity', Size: 'xl' },
]
