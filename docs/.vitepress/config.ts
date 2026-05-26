import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import { baseConfig } from './config-base'

// Public docs site for Modgud. Single tree, single config, two outputs.
//
// The repo-only design notes live in a sibling VitePress site at
// `dev-docs/` — they are never bundled here, and cross-references
// from this site to dev-docs would be external URLs if they existed
// (they shouldn't — public content has no business pointing at
// repo-only design history).
//
// In-app build (the version shipped inside the Docker container)
// is the SAME content — just with a different `base` + sub-shell nav,
// configured in config.in-app.ts. There is no public/in-app
// subsetting; the whole site goes both places.

export default withMermaid(defineConfig(baseConfig))
