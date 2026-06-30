import llmstxt from 'vitepress-plugin-llms'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)

// Shared raw config used by both the public site (`config.ts`) and
// the in-app variant (`config.in-app.ts`).
//
// Why raw (not `defineConfig(...)` wrapped, and not run through
// `withMermaid`) — VitePress 1.6 + vitepress-plugin-mermaid silently
// loses keys when you spread `withMermaid(defineConfig({...}))` and
// then layer overrides on top. `base`, `outDir`, `nav`, `rewrites`,
// `footer` all disappeared from the in-app build, producing a site
// identical to the public one. Keeping this object plain lets both
// entry points spread it cleanly and apply their own deltas before
// wrapping.
export const baseConfig = {
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
    // Persona-explicit nav. Order mirrors a typical visitor journey:
    // discover (Get Started) → understand (Concepts) → run (Operate) →
    // configure (Administer) → integrate apps → support end-users →
    // look things up (Reference) → contribute. /admin/ and /end-user/
    // URL prefixes are intentionally preserved (familiar; widely
    // linked); the sidebar text reflects the persona.
    nav: [
      { text: 'Get Started', link: '/getting-started/' },
      { text: 'Concepts', link: '/concepts/apps-and-resource-access' },
      { text: 'Operate', link: '/operate/deployment' },
      { text: 'Administer', link: '/admin/' },
      { text: 'Integrate', link: '/integrate/resource-server' },
      { text: 'User Help', link: '/end-user/' },
      { text: 'Reference', link: '/reference/oauth-api' },
      { text: 'Contribute', link: '/contribute/developing-locally' },
      { text: 'Roadmap', link: '/roadmap' },
      { text: 'LLM Docs', link: '/llms-full.txt', target: '_blank' },
    ],

    sidebar: {
      '/getting-started/': [
        {
          text: 'Get Started',
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
            { text: 'Dynamic Client Registration', link: '/concepts/dynamic-client-registration' },
            { text: 'Sessions & Tokens', link: '/concepts/tokens' },
          ],
        },
      ],
      '/operate/': [
        {
          text: 'Operate',
          items: [
            { text: 'Docker & Deployment', link: '/operate/deployment' },
            { text: 'Backend layout', link: '/operate/backend-architecture' },
            { text: 'Persistence (Marten)', link: '/operate/database' },
            { text: 'Multi-tenancy / Realms', link: '/operate/realms' },
            { text: 'Observability', link: '/operate/observability' },
            { text: 'Recovery CLI', link: '/operate/recovery-cli' },
            { text: 'Feature Flags', link: '/operate/feature-flags' },
          ],
        },
      ],
      '/admin/': [
        {
          text: 'Administer (Realm-Admin)',
          items: [
            { text: 'Overview', link: '/admin/' },
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
            { text: 'Client ID Metadata Documents', link: '/admin/client-id-metadata-documents' },
            { text: 'Login Providers', link: '/admin/login-providers' },
          ],
        },
        {
          text: 'Realm',
          items: [
            { text: 'Applications', link: '/admin/applications' },
            { text: 'Realms', link: '/admin/realms' },
            { text: 'Declarative Realm Provisioning', link: '/admin/realm-provisioning' },
            { text: 'Realm Settings', link: '/admin/realm-settings' },
            { text: 'Auth Log', link: '/admin/auth-log' },
            { text: 'Scheduled Jobs', link: '/admin/scheduled-jobs' },
            { text: 'Change Requests', link: '/admin/change-requests' },
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
          text: 'Customization',
          items: [
            { text: 'Branding', link: '/plattform/branding' },
            { text: 'Asset Library', link: '/plattform/assets' },
            { text: 'Pages (Beta)', link: '/plattform/pages' },
          ],
        },
        {
          text: 'Operations',
          items: [
            { text: 'Inbox', link: '/plattform/inbox' },
            { text: 'Inbox settings', link: '/plattform/inbox-settings' },
            { text: 'Settings', link: '/plattform/settings' },
          ],
        },
      ],
      '/integrate/': [
        {
          text: 'Integrate',
          items: [
            { text: 'Resource server (.NET)', link: '/integrate/resource-server' },
            { text: 'SaaS app walkthrough', link: '/integrate/saas-walkthrough' },
            { text: 'Native apps (iOS / mobile)', link: '/integrate/native-apps' },
            { text: 'OAuth / OpenIddict', link: '/integrate/oauth' },
            { text: 'Cookies & sessions', link: '/integrate/cookies-and-sessions' },
            { text: 'Login flows', link: '/integrate/login-flows' },
            { text: 'Login providers (OIDC federation)', link: '/integrate/login-providers' },
            { text: '2FA (TOTP, Email, Passkey)', link: '/integrate/two-factor' },
            { text: 'Scheduling (Quartz)', link: '/integrate/scheduling' },
          ],
        },
      ],
      '/end-user/': [
        {
          text: 'User Help',
          items: [
            { text: 'Overview', link: '/end-user/' },
            { text: 'First steps', link: '/end-user/first-steps' },
            { text: 'Sign in', link: '/end-user/sign-in' },
            { text: 'Password', link: '/end-user/password' },
            { text: 'Two-factor', link: '/end-user/two-factor' },
            { text: 'Passkey', link: '/end-user/passkey' },
            { text: 'Profile', link: '/end-user/profile' },
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
      '/contribute/': [
        {
          text: 'Contribute',
          items: [
            { text: 'Developing locally', link: '/contribute/developing-locally' },
            { text: 'Local CI iteration', link: '/contribute/local-ci' },
          ],
        },
        {
          text: 'Testing',
          items: [
            { text: 'Overview', link: '/contribute/testing/' },
            { text: 'Automated tests', link: '/contribute/testing/automated-tests' },
            { text: 'Pinned-by-design', link: '/contribute/testing/pinned-by-design' },
            { text: 'Manual smoke checklist', link: '/contribute/testing/manual-checklist' },
          ],
        },
      ],
    },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/cocoar-dev/modgud' },
    ],

    search: {
      provider: 'local' as const,
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
}
