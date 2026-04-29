import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import llmstxt from 'vitepress-plugin-llms'

export default withMermaid(
  defineConfig({
    title: 'Cocoar.Auth',
    description: 'Identity Provider für die Cocoar SaaS-Plattform — Multi-Realm, OpenIddict, TimeToDo-Slices',
    lang: 'de-DE',

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
        { text: 'Konzepte', link: '/concepts/glossary' },
        { text: 'Architektur', link: '/guide/architecture' },
        { text: 'Authentication-Slice', link: '/authentication-slice/' },
        { text: 'Authorization-Slice', link: '/authorization-slice/' },
        { text: 'API-Referenz', link: '/reference/auth-api' },
        { text: 'LLM Docs', link: '/llms-full.txt', target: '_blank' },
      ],

      sidebar: {
        '/concepts/': [
          {
            text: 'Konzepte',
            items: [
              { text: 'Glossar', link: '/concepts/glossary' },
              { text: 'Realms (Multi-Tenant)', link: '/concepts/realms' },
              { text: 'Authentifizierung', link: '/concepts/authentication' },
              { text: 'Autorisierung & ABAC', link: '/concepts/groups-and-authorization' },
              { text: 'OAuth & OIDC', link: '/concepts/oauth' },
              { text: 'Sessions & Tokens', link: '/concepts/tokens' },
            ],
          },
        ],
        '/guide/': [
          {
            text: 'Einstieg',
            items: [
              { text: 'Überblick', link: '/guide/overview' },
              { text: 'Getting Started (Dev)', link: '/guide/getting-started' },
            ],
          },
          {
            text: 'Architektur',
            items: [
              { text: 'Backend-Aufbau', link: '/guide/architecture' },
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
              { text: 'Vue-Frontend', link: '/guide/frontend' },
            ],
          },
          {
            text: 'Operations',
            items: [
              { text: 'Docker & Deployment', link: '/guide/deployment' },
            ],
          },
        ],
        '/authentication-slice/': [
          {
            text: 'Cocoar.Auth.Authentication',
            items: [
              { text: 'Überblick', link: '/authentication-slice/' },
              { text: 'Konzepte', link: '/authentication-slice/konzepte' },
              { text: 'Login-Flows', link: '/authentication-slice/login-flows' },
              { text: 'Identity-Provider (OIDC)', link: '/authentication-slice/identity-providers' },
              { text: 'GDPR & Sessions', link: '/authentication-slice/gdpr-sessions' },
            ],
          },
        ],
        '/authorization-slice/': [
          {
            text: 'Cocoar.Auth.Authorization',
            items: [
              { text: 'Überblick', link: '/authorization-slice/' },
              { text: 'Konzepte', link: '/authorization-slice/konzepte' },
              { text: 'Permissions & Gating', link: '/authorization-slice/permissions' },
              { text: 'Access Scripts (ABAC)', link: '/authorization-slice/access-scripts' },
              { text: 'Auto-Membership', link: '/authorization-slice/auto-membership' },
            ],
          },
        ],
        '/reference/': [
          {
            text: 'API-Referenz',
            items: [
              { text: 'Auth-Endpoints', link: '/reference/auth-api' },
              { text: 'Admin-Endpoints', link: '/reference/admin-api' },
              { text: 'OAuth-Endpoints', link: '/reference/oauth-api' },
              { text: 'Realm-Endpoints', link: '/reference/realm-api' },
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
        label: 'Auf dieser Seite',
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
  }),
)
