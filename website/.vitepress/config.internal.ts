import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import { publicConfig } from './config'

// Local-dev variant — same as the public config, but re-includes the
// `internal/**` tree (repo-only dev notes: future-features planning,
// architecture-decision drafts, design discussions). Used by `pnpm dev`
// so contributors browsing the docs locally see the internal section
// alongside the published pages.
//
// This config is NEVER used by any build:
//   - Public site uses .vitepress/config.ts
//   - In-app help uses .vitepress/config.in-app.ts
// Both of those exclude internal/**. The internal section is therefore
// only reachable on a developer's localhost via `vitepress dev`.
//
// Convention: when adding a new internal page, register it in the
// sidebar block below so it shows up in the local nav.
const internalConfig = defineConfig({
  ...publicConfig,
  // Re-include internal pages — strip the public-build's exclude.
  srcExclude: [],
  themeConfig: {
    ...publicConfig.themeConfig,
    nav: [
      ...(publicConfig.themeConfig?.nav ?? []),
      { text: '🔒 Internal', link: '/internal/' },
    ],
    sidebar: {
      ...(publicConfig.themeConfig?.sidebar ?? {}),
      '/internal/': [
        {
          text: '🔒 Internal — Dev Notes',
          items: [
            { text: 'Overview', link: '/internal/' },
          ],
        },
        {
          text: 'Future Features',
          items: [
            { text: 'Overview', link: '/internal/future-features/' },
            { text: 'White-label customization', link: '/internal/future-features/white-label-customization' },
            { text: 'Login alerts + IP blacklist', link: '/internal/future-features/login-alerts-ip-blacklist' },
          ],
        },
      ],
    },
  },
})

export default withMermaid(internalConfig)
