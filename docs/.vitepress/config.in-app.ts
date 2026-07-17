import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import { baseConfig } from './config-base'

// In-app docs build — same content as the public site, packaged
// inside the Docker container under /docs/. The version-lock is the
// point: whichever Modgud build ships to a customer carries exactly
// the docs that match its features and APIs.
//
// Public docs (docs.modgud.com or wherever) always reflect "latest";
// in-app docs are always "this version". Same source tree, two
// outputs.
//
// `outDir` is set on the CLI in package.json (`build:in-app` script)
// because VitePress 1.6 ignores inline `outDir` when a custom
// `--config` is passed. The CLI flag is the reliable hook.

export default withMermaid(defineConfig({
  ...baseConfig,
  base: '/docs/',

  // Marketing landing page has no place inside the container — the
  // admin enters at /admin/ directly and the docs/ root is reached
  // via the help link in the admin UI.
  rewrites: {
    'admin/index.md': 'index.md',
  },

  themeConfig: {
    ...baseConfig.themeConfig,
    // Marketing landing nav (Roadmap, LLM Docs, etc.) is noise in-app —
    // override to the admin-facing subset.
    nav: [
      { text: 'Admin', link: '/admin/' },
      { text: 'Platform', link: '/platform/' },
      { text: 'Concepts', link: '/concepts/apps-and-resource-access' },
      { text: 'Reference', link: '/reference/oauth-api' },
    ],
    footer: {
      message: 'In-app help for this Modgud instance.',
    },
  },
}))
