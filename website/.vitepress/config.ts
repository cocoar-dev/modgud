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
    title: 'Cocoar.Auth',
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
        { text: 'Reference', link: '/reference/distribution-api' },
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
            text: 'Frontend',
            items: [
              { text: 'Vue Frontend', link: '/guide/frontend' },
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
              { text: 'Roles', link: '/admin/roles' },
              { text: 'Groups', link: '/admin/groups' },
            ],
          },
          {
            text: 'Apps',
            items: [
              { text: 'Applications', link: '/admin/applications' },
            ],
          },
          {
            text: 'OAuth & OIDC',
            items: [
              { text: 'OAuth Clients', link: '/admin/oauth-clients' },
              { text: 'OAuth Scopes', link: '/admin/oauth-scopes' },
              { text: 'OAuth APIs (Resource Servers)', link: '/admin/oauth-apis' },
            ],
          },
          {
            text: 'Federation & Realms',
            items: [
              { text: 'Login Providers', link: '/admin/login-providers' },
              { text: 'Realms', link: '/admin/realms' },
            ],
          },
          {
            text: 'Operations',
            items: [
              { text: 'Auth Log', link: '/admin/auth-log' },
              { text: 'Change Requests', link: '/admin/change-requests' },
              { text: 'Settings', link: '/admin/settings' },
              { text: 'Recovery CLI', link: '/admin/recovery-cli' },
            ],
          },
        ],
        '/authentication-slice/': [
          {
            text: 'Cocoar.Auth.Authentication',
            items: [
              { text: 'Overview', link: '/authentication-slice/' },
              { text: 'Concepts', link: '/authentication-slice/konzepte' },
              { text: 'Login Flows', link: '/authentication-slice/login-flows' },
              { text: 'Login Providers (OIDC)', link: '/authentication-slice/login-providers' },
              { text: 'GDPR & Sessions', link: '/authentication-slice/gdpr-sessions' },
            ],
          },
        ],
        '/authorization-slice/': [
          {
            text: 'Cocoar.Auth.Authorization',
            items: [
              { text: 'Overview', link: '/authorization-slice/' },
              { text: 'Concepts', link: '/authorization-slice/konzepte' },
              { text: 'Permissions & Gating', link: '/authorization-slice/permissions' },
              { text: 'Auto-Membership', link: '/authorization-slice/auto-membership' },
            ],
          },
        ],
        '/reference/': [
          {
            text: 'API Reference',
            items: [
              { text: 'Distribution API', link: '/reference/distribution-api' },
              { text: 'Auth Endpoints', link: '/reference/auth-api' },
              { text: 'Admin Endpoints', link: '/reference/admin-api' },
              { text: 'OAuth Endpoints', link: '/reference/oauth-api' },
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
        { icon: 'github', link: 'https://github.com/cocoar-dev/Cocoar.Auth' },
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
            { text: 'White-label customization', link: '/dev-notes/future-features/white-label-customization' },
            { text: 'Login alerts + IP blacklist', link: '/dev-notes/future-features/login-alerts-ip-blacklist' },
            { text: 'DCR for MCP clients', link: '/dev-notes/future-features/dcr-for-mcp-clients' },
          ],
        },
      ],
    },
  },
}))
