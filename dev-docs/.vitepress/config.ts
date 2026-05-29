import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)

// Dev-docs VitePress site — repo-only design notes, never deployed.
// Run locally with `pnpm dev` (or via the docs/ workspace) and rendered
// MD also reads fine on GitHub for anyone who clones the repo.
//
// Lives intentionally separate from the public docs/ site so cross-
// references between dev-docs and public docs cannot break silently —
// they would have to be explicit external URLs.

export default withMermaid(defineConfig({
  title: 'Modgud — Dev Notes',
  description: 'Internal design notes, future-feature drafts, and upstream feature-requests.',
  lang: 'en-US',

  // The repo-only nature of this site means link targets are mostly
  // local. Two classes of links are intentionally not resolvable here:
  //
  // 1. Localhost / *.local examples in some pages — illustration only.
  // 2. References to the public docs/ site (concepts, guide, admin,
  //    reference, plattform, getting-started, end-user, testing,
  //    roadmap). Those live in a sibling VitePress instance and the
  //    targets DO exist there — they're just not part of this build.
  //    When docs.modgud.com is up these should become absolute URLs,
  //    but for now the pragmatic move is to ignore them so the build
  //    stays green and real broken links inside dev-docs still fail.
  ignoreDeadLinks: [
    /^https?:\/\/localhost/,
    /^https?:\/\/127\.0\.0\.1/,
    /^https?:\/\/[a-z0-9.-]+\.(?:dev|local|localhost|invalid)/,
    /^\/(concepts|admin|plattform|reference|end-user|getting-started|operate|integrate|contribute|roadmap)(\/|$)/,
    /^\.\.?\/.*\/(concepts|admin|plattform|reference|end-user|getting-started|operate|integrate|contribute|roadmap|index)(\/|$)?/,
  ],

  vite: {
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
    nav: [
      { text: 'Overview', link: '/' },
      { text: 'Future Features', link: '/future-features/' },
      { text: 'Upstream Requests', link: '/upstream-feature-requests/' },
    ],

    sidebar: [
      {
        text: '🔒 Dev Notes',
        items: [
          { text: 'Overview', link: '/' },
          { text: 'README (conventions)', link: '/README' },
        ],
      },
      {
        text: 'Future Features',
        items: [
          { text: 'Overview', link: '/future-features/' },
          { text: '⭐ Identity-Lifecycle Untangle + Federation group-sync', link: '/future-features/identity-lifecycle-untangle' },
          { text: '⭐ Federation v1 — Implementation Spec', link: '/future-features/federation-v1-design' },
          { text: '⭐ Production-Readiness Audit 2026-05-13', link: '/future-features/production-readiness-audit-2026-05-13' },
          { text: 'HA / Multi-Instance Readiness', link: '/future-features/ha-multi-instance' },
          { text: 'Realm Backup / Restore / DR', link: '/future-features/realm-backup-restore' },
          { text: 'Enterprise SSO — SAML + LDAP', link: '/future-features/enterprise-sso-saml-ldap' },
          { text: 'SAML federation — implementation plan', link: '/future-features/saml-federation' },
          { text: 'SAML AMR → amr wiring (deferred I15)', link: '/future-features/saml-amr-wiring' },
          { text: 'Multi-IdP login UX', link: '/future-features/multi-idp-login-ux' },
          { text: 'Login-Providers UI refactor (single-modal)', link: '/future-features/login-providers-ui-refactor' },
          { text: 'White-label customization (Phase 2)', link: '/future-features/white-label-customization' },
          { text: 'Login alerts + IP blacklist', link: '/future-features/login-alerts-ip-blacklist' },
          { text: 'App as permission catalog; RS gets subset', link: '/future-features/app-resources-as-permissions' },
          { text: 'Permission-Modell (finaler Stand)', link: '/future-features/permission-modell' },
          { text: 'Permission-Modell — Adversarial Review', link: '/future-features/permission-modell-adversarial-review' },
          { text: 'UserInfo Hybrid-Emission (Single-Aud)', link: '/future-features/userinfo-hybrid-flat-emission' },
          { text: 'Per-App Login-Customization', link: '/future-features/per-app-login-customization' },
          { text: 'Page-builder runtime', link: '/future-features/page-builder-runtime' },
          { text: 'Service-account credentials', link: '/future-features/service-account-credentials' },
          { text: 'NodaTime migration', link: '/future-features/nodatime-migration' },
          { text: 'CI iteration hygiene', link: '/future-features/ci-iteration-hygiene' },
        ],
      },
      {
        text: 'Engineering gotchas',
        items: [
          { text: 'Critter Stack 2026', link: '/engineering-gotchas/critter-stack-2026' },
          { text: 'Marten raise side-effects', link: '/engineering-gotchas/marten-raise-side-effects' },
        ],
      },
      {
        text: 'Architecture',
        items: [
          { text: 'Overview', link: '/architecture/' },
          { text: 'Authentication slice', link: '/architecture/authentication' },
          { text: 'Authorization slice', link: '/architecture/authorization' },
        ],
      },
      {
        text: 'Other',
        items: [
          { text: 'Frontend dev notes', link: '/frontend' },
          { text: 'JsEval threat model', link: '/jseval-threat-model' },
          { text: 'Security hardening tracker', link: '/security-hardening' },
          { text: 'CodeQL triage (initial sweep)', link: '/codeql-triage' },
        ],
      },
      {
        text: 'Upstream feature-requests',
        items: [
          { text: 'Overview', link: '/upstream-feature-requests/' },
          { text: '@cocoar/vue-ui — Sidebar aria-label', link: '/upstream-feature-requests/vue-ui-sidebar-item-aria-label' },
          { text: '@cocoar/vue-ui — Listbox toggle mode', link: '/upstream-feature-requests/vue-ui-listbox-cumulative-highlight' },
          { text: '@cocoar/vue-page-builder — styles export', link: '/upstream-feature-requests/vue-page-builder-styles-export' },
        ],
      },
    ],

    outline: {
      label: 'On this page',
    },

    search: {
      provider: 'local',
    },

    footer: {
      message: 'Repo-only. Not deployed.',
    },
  },

  mermaid: {},

  mermaidPlugin: {
    class: 'mermaid',
  },
}))
