import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import llmstxt from 'vitepress-plugin-llms'

// Public docs build — the full documentation tree shipped on the
// public docs site. Includes everything: marketing landing, getting
// started, concepts, integration guides, reference, and the admin
// operations surface (which is also bundled into the in-app build via
// config.in-app.ts).
//
// THIS FILE IS THE DEFAULT VITEPRESS CONFIG ("config.ts" is what
// `vitepress dev` and `vitepress build` look up by convention).
//
// Three configs sit in this directory:
//   - config.ts          — DEV variant, dev-notes/** is VISIBLE.
//                          Used by `pnpm dev` (no --config flag).
//   - config.public.ts   — PUBLIC build, dev-notes/** is EXCLUDED.
//                          Used by `pnpm build` (with explicit --config).
//   - config.in-app.ts   — IN-APP help build, also excludes dev-notes/**.
//                          Used by `pnpm build:in-app` and the Dockerfile.
//
// Why this inverted layout (dev = default, publish = explicit --config)?
// Because we hit a VitePress quirk: `srcExclude` set in config.ts leaks
// into builds that explicitly use --config to point at a different file.
// Putting srcExclude in config.ts therefore made dev-notes/** invisible
// even with the dedicated dev-notes config. The fix: keep config.ts
// exclude-free, move the publish-time exclusion into a separate config
// file that doesn't get auto-loaded.
//
// The downside is that someone who runs `vitepress build` directly
// (bypassing `pnpm build`) would publish dev-notes/. Mitigated by the
// pnpm scripts always using --config explicitly.
//
// (Historical note: dev-notes/ was originally at /internal/ but VitePress
// silently skips that directory name — possibly via vitepress-plugin-llms
// or another bundled plugin's hardcoded ignore. Renamed to dev-notes/
// after the issue surfaced; the semantic intent is identical.)
export const baseConfig = defineConfig({
    title: 'Modgud',
    description: 'Multi-Tenant Identity Provider — OAuth 2.0 / OpenID Connect, multi-app permissions, granular RBAC, GDPR-ready.',
    lang: 'en-US',

    // Localhost / *.local references in the quickstart and troubleshooting
    // sections are intentional examples, not broken links.
    ignoreDeadLinks: [
      /^https?:\/\/localhost/,
      /^https?:\/\/127\.0\.0\.1/,
      /^https?:\/\/[a-z0-9.-]+\.(?:dev|local|localhost|invalid)/,
    ],

    head: [
      ['link', { rel: 'icon', type: 'image/svg+xml', href: '/logo_light.svg' }],
      ['link', { rel: 'alternate', type: 'text/plain', href: '/llms.txt', title: 'LLM documentation (summary)' }],
      ['link', { rel: 'alternate', type: 'text/plain', href: '/llms-full.txt', title: 'LLM documentation (full)' }],
    ],

    vite: {
      plugins: [llmstxt({
        excludeUnnecessaryFiles: false,
        ignoreFiles: ['changelog.md'],
      })],
    },

    themeConfig: {
      logo: {
        light: '/logo_light.svg',
        dark: '/logo_dark.svg',
      },
      nav: [
        { text: 'Getting Started', link: '/getting-started/' },
        { text: 'Concepts', link: '/concepts/apps-and-resource-access' },
        { text: 'Guide', link: '/guide/integrating-resource-server' },
        { text: 'Admin', link: '/admin/' },
        { text: 'Plattform', link: '/plattform/' },
        { text: 'Reference', link: '/reference/oauth-api' },
        { text: 'Testing', link: '/testing/' },
        { text: 'LLM Docs', link: '/llms-full.txt', target: '_blank' },
      ],

      sidebar: {
        '/getting-started/': [
          {
            text: 'Getting Started',
            items: [
              { text: 'Overview', link: '/getting-started/' },
              { text: 'Quickstart (Docker)', link: '/getting-started/quickstart' },
              { text: 'Requirements', link: '/getting-started/requirements' },
              { text: 'Features', link: '/getting-started/features' },
              { text: 'First-time setup', link: '/getting-started/first-time-setup' },
              { text: 'Single-tenant mode', link: '/getting-started/single-tenant-mode' },
            ],
          },
        ],
        '/concepts/': [
          {
            text: 'Concepts',
            items: [
              { text: 'Glossary', link: '/concepts/glossary' },
              { text: 'Apps & resource_access', link: '/concepts/apps-and-resource-access' },
              { text: 'Realms (Multi-Tenant)', link: '/concepts/realms' },
              { text: 'Control Plane / Data Plane', link: '/concepts/control-plane' },
              { text: 'Authentication', link: '/concepts/authentication' },
              { text: 'Authorization (RBAC)', link: '/concepts/groups-and-authorization' },
              { text: 'ABAC and the IAM boundary', link: '/concepts/abac' },
              { text: 'OAuth & OIDC', link: '/concepts/oauth' },
              { text: 'Sessions & Tokens', link: '/concepts/tokens' },
            ],
          },
        ],
        '/guide/': [
          {
            text: 'Integration',
            items: [
              { text: 'Integrating a Resource Server', link: '/guide/integrating-resource-server' },
            ],
          },
          {
            text: 'Architecture',
            items: [
              { text: 'Overview', link: '/guide/overview' },
              { text: 'Backend Layout', link: '/guide/architecture' },
              { text: 'Multi-Tenancy / Realms', link: '/guide/realms' },
              { text: 'Persistence (Marten)', link: '/guide/database' },
              { text: 'OAuth / OpenIddict', link: '/guide/oauth' },
            ],
          },
          {
            text: 'Authentication',
            items: [
              { text: 'Cookies & Sessions', link: '/guide/auth-cookies' },
              { text: '2FA (TOTP, Email, Passkey)', link: '/guide/two-factor' },
            ],
          },
          {
            text: 'Scheduling & Background Work',
            items: [
              { text: 'Quartz Jobs', link: '/guide/scheduling' },
            ],
          },
          {
            text: 'Operations',
            items: [
              { text: 'Docker & Deployment', link: '/guide/deployment' },
            ],
          },
        ],
        '/admin/': [
          {
            text: 'Admin Operations',
            items: [
              { text: 'Overview', link: '/admin/' },
              { text: 'SaaS App Integration Walkthrough', link: '/admin/saas-integration-walkthrough' },
            ],
          },
          {
            text: 'Identity & Access',
            items: [
              { text: 'Users', link: '/admin/users' },
              { text: 'Service Accounts', link: '/admin/service-accounts' },
              { text: 'Roles', link: '/admin/roles' },
              { text: 'Groups', link: '/admin/groups' },
            ],
          },
          {
            text: 'OAuth & Federation',
            items: [
              { text: 'OAuth Clients', link: '/admin/oauth-clients' },
              { text: 'OAuth Scopes', link: '/admin/oauth-scopes' },
              { text: 'OAuth APIs (Resource Servers)', link: '/admin/oauth-apis' },
              { text: 'Dynamic Client Registration', link: '/admin/dynamic-client-registration' },
              { text: 'Login Providers', link: '/admin/login-providers' },
            ],
          },
          {
            text: 'System',
            items: [
              { text: 'Applications', link: '/admin/applications' },
              { text: 'Realms', link: '/admin/realms' },
              { text: 'Realm Settings', link: '/admin/realm-settings' },
              { text: 'Auth Log', link: '/admin/auth-log' },
              { text: 'Scheduled Jobs', link: '/admin/scheduled-jobs' },
              { text: 'Change Requests', link: '/admin/change-requests' },
            ],
          },
          {
            text: 'Tools',
            items: [
              { text: 'Feature Flags', link: '/admin/feature-flags' },
              { text: 'Recovery CLI', link: '/admin/recovery-cli' },
            ],
          },
        ],
        '/plattform/': [
          {
            text: 'Plattform',
            items: [
              { text: 'Overview', link: '/plattform/' },
            ],
          },
          {
            text: 'Anpassung',
            items: [
              { text: 'Branding', link: '/plattform/branding' },
              { text: 'Asset Library', link: '/plattform/assets' },
              { text: 'Pages (Beta)', link: '/plattform/pages' },
            ],
          },
          {
            text: 'Betrieb',
            items: [
              { text: 'Observability', link: '/plattform/observability' },
              { text: 'Inbox', link: '/plattform/inbox' },
              { text: 'Inbox-Einstellungen', link: '/plattform/inbox-settings' },
              { text: 'App-Einstellungen', link: '/plattform/settings' },
            ],
          },
        ],
        '/reference/': [
          {
            text: 'API Reference',
            items: [
              { text: 'OAuth Endpoints', link: '/reference/oauth-api' },
              { text: 'Auth Endpoints', link: '/reference/auth-api' },
              { text: 'Admin Endpoints', link: '/reference/admin-api' },
              { text: 'Realm Endpoints', link: '/reference/realm-api' },
            ],
          },
        ],
        '/testing/': [
          {
            text: 'Testing',
            items: [
              { text: 'Overview', link: '/testing/' },
              { text: 'Automated tests', link: '/testing/automated-tests' },
              { text: 'Pinned-by-design', link: '/testing/pinned-by-design' },
              { text: 'Manual smoke checklist', link: '/testing/manual-checklist' },
              { text: 'Security Hardening Tracker', link: '/testing/security-hardening' },
              { text: 'JsEval Threat Model', link: '/testing/jseval-threat-model' },
            ],
          },
        ],
      },

      socialLinks: [
        { icon: 'github', link: 'https://github.com/cocoar-dev/Modgud' },
      ],

      search: {
        provider: 'local',
      },

      outline: {
        label: 'On this page',
      },

      footer: {
        message: 'Released under the Apache-2.0 License.',
        copyright: 'Copyright 2025-present Cocoar',
      },
    },

    mermaid: {},

    mermaidPlugin: {
      class: 'mermaid',
    },
})

// Default export — config.ts is the DEV variant: includes the
// dev-notes/** tree in nav + sidebar so `pnpm dev` shows it.
// The PUBLIC build uses config.public.ts which excludes dev-notes.
export default withMermaid(defineConfig({
  ...baseConfig,
  themeConfig: {
    ...baseConfig.themeConfig,
    nav: [
      ...(baseConfig.themeConfig?.nav ?? []),
      { text: '🔒 Dev Notes', link: '/dev-notes/' },
    ],
    sidebar: {
      ...(baseConfig.themeConfig?.sidebar ?? {}),
      '/dev-notes/': [
        {
          text: '🔒 Dev Notes',
          items: [
            { text: 'Overview', link: '/dev-notes/' },
          ],
        },
        {
          text: 'Future Features',
          items: [
            { text: 'Overview', link: '/dev-notes/future-features/' },
            { text: '⭐ Production-Readiness Audit 2026-05-13', link: '/dev-notes/future-features/production-readiness-audit-2026-05-13' },
            { text: 'HA / Multi-Instance Readiness', link: '/dev-notes/future-features/ha-multi-instance' },
            { text: 'Realm Backup / Restore / DR', link: '/dev-notes/future-features/realm-backup-restore' },
            { text: 'Enterprise SSO — SAML + LDAP', link: '/dev-notes/future-features/enterprise-sso-saml-ldap' },
            { text: 'White-label customization (Phase 2)', link: '/dev-notes/future-features/white-label-customization' },
            { text: 'Login alerts + IP blacklist', link: '/dev-notes/future-features/login-alerts-ip-blacklist' },
            { text: 'App as permission catalog; RS gets subset', link: '/dev-notes/future-features/app-resources-as-permissions' },
            { text: 'Permission-Modell (finaler Stand)', link: '/dev-notes/future-features/permission-modell' },
            { text: 'Permission-Modell — Adversarial Review', link: '/dev-notes/future-features/permission-modell-adversarial-review' },
            { text: 'UserInfo Hybrid-Emission (Single-Aud)', link: '/dev-notes/future-features/userinfo-hybrid-flat-emission' },
            { text: 'Per-App Login-Customization (Routing + Form-Builder)', link: '/dev-notes/future-features/per-app-login-customization' },
          ],
        },
        {
          text: 'Upstream feature-requests',
          items: [
            { text: 'Overview', link: '/dev-notes/upstream-feature-requests/' },
            { text: '@cocoar/vue-ui — Sidebar aria-label', link: '/dev-notes/upstream-feature-requests/vue-ui-sidebar-item-aria-label' },
            { text: '@cocoar/vue-ui — Listbox toggle mode', link: '/dev-notes/upstream-feature-requests/vue-ui-listbox-cumulative-highlight' },
            { text: '@cocoar/vue-page-builder — styles export', link: '/dev-notes/upstream-feature-requests/vue-page-builder-styles-export' },
          ],
        },
      ],
    },
  },
}))
