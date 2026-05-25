import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import publicConfig from './config'

// In-app docs build — same content as the public site, packaged
// inside the Docker container under /docs/. The version-lock is the
// point: whichever Modgud build ships to a customer carries exactly
// the docs that match its features and APIs.
//
// Public docs (docs.modgud.com or wherever) always reflect "latest";
// in-app docs are always "this version". Same source tree, two
// outputs.

const base = publicConfig as ReturnType<typeof defineConfig>

export default withMermaid(defineConfig({
  ...base,
  base: '/docs/',
  outDir: '.vitepress/dist-in-app',

  // Marketing landing page has no place inside the container — the
  // admin enters at /admin/ directly and the docs/ root is reached
  // via the help link in the admin UI.
  rewrites: {
    'admin/index.md': 'index.md',
  },

  // The in-app build inherits the public site's themeConfig, but
  // the marketing landing nav (Roadmap, LLM Docs, etc.) is noise
  // in-app. Override the nav to the admin-facing subset.
  themeConfig: {
    ...base.themeConfig,
    nav: [
      { text: 'Admin', link: '/admin/' },
      { text: 'Plattform', link: '/plattform/' },
      { text: 'Concepts', link: '/concepts/apps-and-resource-access' },
      { text: 'Reference', link: '/reference/oauth-api' },
    ],
    footer: {
      message: 'In-app help for this Modgud instance.',
    },
  },
}))
