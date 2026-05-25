import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import llmstxt from 'vitepress-plugin-llms'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)

// Public docs site for Modgud. Single tree, single config, one build.
// The repo-only design notes live in a sibling VitePress site at
// `dev-docs/` — they are never bundled here, and cross-references
// from this site to dev-docs would be external URLs if they existed
// (they shouldn't — public content has no business pointing at
// repo-only design history).
//
// In-app build (the version shipped inside the Docker container)
// is the SAME content — just with a different `base` + `outDir`,
// configured in config.in-app.ts. There is no public/in-app
// subsetting; the whole site goes both places.

export default withMermaid(defineConfig({
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
    // Mermaid 11.x depends on dayjs which is CJS-only — dayjs's package.json
    // has `main: "dayjs.min.js"` (CJS) and no `"exports"` field, so a
    // bare `import dayjs from 'dayjs'` lands on the CJS file. In Vite's
    // dev mode the browser then gets a default-import that fails because
    // CJS modules don't expose `default`. Route every dayjs import to the
    // ESM build that ships in the same package. Production build is
    // unaffected (rollup handles CJS fine).
    resolve: {
      // Exact-match regex so only the bare `import dayjs from 'dayjs'`
      // is rewritten — sub-paths like `dayjs/plugin/isoWeek` must
      // resolve normally.
      alias: [
        { find: /^dayjs$/, replacement: require.resolve('dayjs/esm/index.js') },
      ],
    },
    optimizeDeps: {
      include: ['dayjs'],
    },
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
      { text: 'Roadmap', link: '/roadmap' },
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
            { text: 'Permissions & gating', link: '/concepts/permissions' },
            { text: 'Auto-Membership', link: '/concepts/auto-membership' },
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
            { text: 'Login flows', link: '/guide/login-flows' },
            { text: 'Login providers (OIDC federation)', link: '/guide/login-providers' },
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
        {
          text: 'Contributing',
          items: [
            { text: 'Developing locally', link: '/guide/developing-locally' },
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
}))
