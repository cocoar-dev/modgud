import { defineConfig } from 'vitepress'

// In-app docs build — what an authenticated admin sees under /docs/
// inside the running application. Same source tree as the public docs,
// filtered to admin operations + end-user help only. Marketing,
// concepts, integration guides, internal slice docs, and reference
// pages are excluded; the admin enters at /admin/ directly.
//
// Build target lives at .vitepress/dist-in-app so it's distinguishable
// from the public build's dist/.
export default defineConfig({
  title: 'Modgud — Help',
  description: 'In-application help for the running Modgud instance.',
  lang: 'en-US',
  base: '/docs/',
  outDir: '.vitepress/dist-in-app',

  // The srcExclude above intentionally drops marketing/concepts/guide/
  // reference pages, but the included admin/end-user pages still link
  // to some of them. The public build catches dead links; the in-app
  // build is content-filtered by design and tolerates them.
  ignoreDeadLinks: true,

  // Drop everything that isn't admin operations or end-user help.
  // The public landing page and marketing nav have no place in-app.
  // `dev-notes/**` is also excluded here — those are repo-only dev
  // notes that must never ship in any deployed artifact.
  srcExclude: [
    'index.md',
    'getting-started/**',
    'features.md',
    'requirements.md',
    'concepts/**',
    'guide/**',
    'reference/**',
    'authentication-slice/**',
    'authorization-slice/**',
    'dev-notes/**',
  ],

  // The public landing has the marketing pitch; in-app, route the
  // root straight to /admin/ where the admin actually wants to be.
  rewrites: {
    'admin/index.md': 'index.md',
  },

  themeConfig: {
    logo: {
      light: '/logo_light.svg',
      dark: '/logo_dark.svg',
    },

    nav: [
      { text: 'Admin', link: '/admin/' },
      { text: 'Plattform', link: '/plattform/' },
      { text: 'End-user help', link: '/end-user/' },
    ],

    sidebar: {
      // Mirror the public-build admin sidebar so admins reading inside
      // the app find the same structure documented externally.
      '/': [
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
        {
          text: 'For end users',
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
    },

    search: {
      provider: 'local',
    },

    outline: {
      label: 'On this page',
    },

    footer: {
      message: 'In-app help for this Modgud instance.',
    },
  },
})
