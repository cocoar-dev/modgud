import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import { baseConfig } from './config-base'

// Public docs site for Modgud. Single tree, single config, two outputs.
//
// This site is the ONLY documentation tree in the repo — it serves end
// users, admins, integrators and (slim) contributor content. Internal
// design notes live in the maintainers' knowledge base, not in the repo,
// and public content never points at them.
//
// In-app build (the version shipped inside the Docker container)
// is the SAME content — just with a different `base` + sub-shell nav,
// configured in config.in-app.ts. There is no public/in-app
// subsetting; the whole site goes both places.

export default withMermaid(defineConfig(baseConfig))
