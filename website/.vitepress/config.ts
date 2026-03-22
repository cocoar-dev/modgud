import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import llmstxt from 'vitepress-plugin-llms'

export default withMermaid(
  defineConfig({
    title: 'Cocoar.Auth',
    description: 'Multi-tenant Identity Provider built with ASP.NET Core, Marten, and Vue',

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
      { text: 'Concepts', link: '/concepts/glossary' },
      { text: 'User Guide', link: '/user-guide/realms' },
      { text: 'Developer Guide', link: '/guide/overview' },
      { text: 'API Reference', link: '/reference/auth-api' },
      { text: 'Roadmap', link: '/todo' },
    ],

    sidebar: {
      '/concepts/': [
        {
          text: 'Concepts',
          items: [
            { text: 'Glossary', link: '/concepts/glossary' },
            { text: 'Realms', link: '/concepts/realms' },
            { text: 'Authentication Model', link: '/concepts/authentication' },
            { text: 'OAuth & OIDC', link: '/concepts/oauth' },
            { text: 'Tokens & Sessions', link: '/concepts/tokens' },
          ],
        },
      ],
      '/user-guide/': [
        {
          text: 'Getting Started',
          items: [
            { text: 'First-Time Setup', link: '/user-guide/first-setup' },
          ],
        },
        {
          text: 'Realm Management',
          items: [
            { text: 'Managing Realms', link: '/user-guide/realms' },
            { text: 'Realm Setup Flow', link: '/user-guide/realm-setup' },
          ],
        },
        {
          text: 'User & Role Management',
          items: [
            { text: 'Managing Users', link: '/user-guide/users' },
            { text: 'Managing Roles', link: '/user-guide/roles' },
          ],
        },
        {
          text: 'OAuth / OIDC Configuration',
          items: [
            { text: 'Registering Clients', link: '/user-guide/clients' },
            { text: 'Scopes & Permissions', link: '/user-guide/scopes' },
            { text: 'APIs', link: '/user-guide/api-resources' },
            { text: 'Client Flows', link: '/user-guide/client-flows' },
          ],
        },
        {
          text: 'External Login',
          items: [
            { text: 'Login Providers', link: '/user-guide/login-providers' },
            { text: 'External Login', link: '/user-guide/external-login' },
          ],
        },
        {
          text: 'Security',
          items: [
            { text: 'Two-Factor Authentication', link: '/user-guide/two-factor' },
            { text: 'Session Management', link: '/user-guide/sessions' },
            { text: 'Privacy & Data Protection', link: '/user-guide/privacy' },
          ],
        },
      ],
      '/guide/': [
        {
          text: 'Introduction',
          items: [
            { text: 'Overview', link: '/guide/overview' },
            { text: 'Getting Started', link: '/guide/getting-started' },
          ],
        },
        {
          text: 'Architecture',
          items: [
            { text: 'Clean Architecture', link: '/guide/architecture' },
            { text: 'CQRS & Event Sourcing', link: '/guide/cqrs-event-sourcing' },
            { text: 'Multi-Tenancy / Realms', link: '/guide/realms' },
          ],
        },
        {
          text: 'Authentication',
          items: [
            { text: 'Cookie-Based Auth', link: '/guide/auth-cookies' },
            { text: 'Two-Factor (TOTP, Email, WebAuthn)', link: '/guide/two-factor' },
            { text: 'OAuth / OpenID Connect', link: '/guide/oauth' },
          ],
        },
        {
          text: 'Frontend',
          items: [
            { text: 'Vue Frontend', link: '/guide/frontend' },
            { text: 'Realm-Aware SPA', link: '/guide/frontend-realms' },
          ],
        },
        {
          text: 'Operations',
          items: [
            { text: 'Docker & Deployment', link: '/guide/deployment' },
            { text: 'Database & Migrations', link: '/guide/database' },
          ],
        },
      ],
      '/reference/': [
        {
          text: 'API Reference',
          items: [
            { text: 'Auth Endpoints', link: '/reference/auth-api' },
            { text: 'Admin Endpoints', link: '/reference/admin-api' },
            { text: 'Realm Endpoints', link: '/reference/realm-api' },
            { text: 'OAuth Endpoints', link: '/reference/oauth-api' },
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
