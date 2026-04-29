import { defineConfig } from 'vitepress'

// Served by the cocoar.auth backend under /docs/ — base matches the serve path so
// absolute asset URLs resolve correctly when proxied behind the API.
export default defineConfig({
  title: 'cocoar.auth — Hilfe',
  description: 'Benutzerhandbuch für cocoar.auth',
  lang: 'de-DE',
  base: '/docs/',

  themeConfig: {
    siteTitle: 'cocoar.auth — Hilfe',

    nav: [
      { text: 'Startseite', link: '/' },
      { text: 'Erste Schritte', link: '/erste-schritte' },
      { text: 'Anmelden', link: '/anmelden' },
      { text: 'Profil', link: '/profil' },
      { text: 'Administration', link: '/admin/' },
    ],

    // Path-scoped sidebars: URLs under /admin/ get the admin sidebar, everything
    // else gets the default user sidebar. Both are reachable via the top nav.
    sidebar: {
      '/admin/': [
        {
          text: 'Administration',
          items: [
            { text: 'Überblick', link: '/admin/' },
            { text: 'Benutzer', link: '/admin/benutzer' },
            { text: 'Rollen & Berechtigungen', link: '/admin/rollen' },
            { text: 'Authorization-Gruppen', link: '/admin/gruppen' },
          ],
        },
        {
          text: 'OAuth & OpenID Connect',
          items: [
            { text: 'OAuth-Clients', link: '/admin/oauth-clients' },
            { text: 'OAuth-Scopes', link: '/admin/oauth-scopes' },
            { text: 'OAuth-APIs (Resource-Server)', link: '/admin/oauth-apis' },
          ],
        },
        {
          text: 'Identitäten & Föderation',
          items: [
            { text: 'Login-Provider', link: '/admin/login-provider' },
            { text: 'Identity Provider (SSO)', link: '/admin/identity-provider' },
            { text: 'Realms (Multi-Tenant)', link: '/admin/realms' },
          ],
        },
        {
          text: 'Betrieb',
          items: [
            { text: 'Anmelde-Log', link: '/admin/auth-log' },
            { text: 'Änderungsanfragen', link: '/admin/aenderungsanfragen' },
            { text: 'Notfall-Recovery (CLI)', link: '/admin/notfall-recovery' },
            { text: 'App-Einstellungen', link: '/admin/einstellungen' },
          ],
        },
      ],

      '/': [
        {
          text: 'Einstieg',
          items: [
            { text: 'Willkommen', link: '/' },
            { text: 'Erste Schritte', link: '/erste-schritte' },
            { text: 'Anmelden', link: '/anmelden' },
          ],
        },
        {
          text: 'Konto & Sicherheit',
          items: [
            { text: 'Profil & Daten', link: '/profil' },
            { text: 'Passwort', link: '/passwort' },
            { text: 'Zwei-Faktor (2FA)', link: '/zwei-faktor' },
            { text: 'Passkey', link: '/passkey' },
          ],
        },
      ],
    },

    outline: {
      label: 'Auf dieser Seite',
    },

    search: {
      provider: 'local',
      options: {
        translations: {
          button: { buttonText: 'Suchen', buttonAriaLabel: 'Suchen' },
          modal: {
            displayDetails: 'Details anzeigen',
            resetButtonTitle: 'Zurücksetzen',
            backButtonTitle: 'Zurück',
            noResultsText: 'Keine Ergebnisse für',
            footer: {
              selectText: 'auswählen',
              navigateText: 'navigieren',
              closeText: 'schließen',
            },
          },
        },
      },
    },

    docFooter: {
      prev: 'Vorherige Seite',
      next: 'Nächste Seite',
    },

    footer: {
      message: 'cocoar.auth Benutzerhandbuch',
      copyright: 'Copyright 2025-present COCOAR e.U.',
    },
  },
})
