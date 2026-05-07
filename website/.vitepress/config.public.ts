import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'
import llmstxt from 'vitepress-plugin-llms'
import fs from 'node:fs'
import path from 'node:path'
import { baseConfig } from './config'

// Public-build config — what gets uploaded to the public docs site.
//
// Spreads baseConfig from config.ts (so the entire public sidebar/nav
// stays single-source-of-truth) and adds the dev-notes belt + braces:
//
//   1. `srcExclude: ['dev-notes/**']` — should keep dev-notes pages
//      out of the build entirely. In our VitePress version (1.6.4) this
//      hasn't proven reliable when the config inherits via spread, so:
//   2. `buildEnd` hook deletes `<outDir>/dev-notes/` after every build
//      — a defence-in-depth that guarantees the published artifact is
//      clean even if srcExclude silently misses something.
//
// Used by `pnpm build` via explicit `--config .vitepress/config.public.ts`.
export default withMermaid(defineConfig({
  ...baseConfig,
  // Same llms-plugin as config.ts; not inherited via baseConfig because
  // baseConfig.vite is for the dev variant and we want to be explicit.
  vite: {
    plugins: [llmstxt({
      excludeUnnecessaryFiles: false,
      ignoreFiles: ['changelog.md', 'dev-notes/**'],
    })],
  },
  srcExclude: ['dev-notes/**'],

  // Belt-and-braces — physically remove the dev-notes tree from the
  // build output. Runs whether srcExclude worked or not.
  buildEnd(siteConfig) {
    const devNotesDir = path.join(siteConfig.outDir, 'dev-notes')
    if (fs.existsSync(devNotesDir)) {
      fs.rmSync(devNotesDir, { recursive: true, force: true })
    }
    // Also kill any leaked .md source-copy artifacts at top-level
    // (vitepress sometimes emits source .md alongside the rendered .html)
    const stragglerDevNotesMd = path.join(siteConfig.outDir, 'dev-notes.md')
    if (fs.existsSync(stragglerDevNotesMd)) {
      fs.unlinkSync(stragglerDevNotesMd)
    }
  },
}))
