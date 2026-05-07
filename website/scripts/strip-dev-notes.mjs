#!/usr/bin/env node
/**
 * Post-build cleanup: physically remove the `dev-notes/` tree from a
 * VitePress build output directory.
 *
 * Why: dev-notes/ is repo-only — design discussions, future-features
 * planning. It must NEVER ship in a published or in-app build. The
 * obvious mechanism — VitePress's `srcExclude` — turned out to be
 * unreliable in our setup (1.6.4 + plugin-mermaid + plugin-llms): the
 * pattern `dev-notes/**` is silently ignored when the config inherits
 * via spread from a shared baseConfig, and the buildEnd hook isn't
 * called when the config is wrapped by withMermaid. Rather than chase
 * the upstream bug, this script just removes the tree from the dist
 * after the build completes — defence in depth, no surprises.
 *
 * Usage:  node scripts/strip-dev-notes.mjs <dist-dir>
 *
 * Removes:
 *   - <dist-dir>/dev-notes/                   (the rendered HTML tree)
 *   - <dist-dir>/dev-notes.md                 (top-level source-copy)
 *   - <dist-dir>/hashmap.json                 (regenerated below)
 *   - any .md sources copied next to .html under dev-notes/ in dist
 *
 * If <dist-dir>/dev-notes/ doesn't exist, exits silently — the script
 * is idempotent and safe to run on a build that didn't include
 * dev-notes (e.g. a future VitePress release that honours srcExclude).
 */
import fs from 'node:fs'
import path from 'node:path'

const distDir = process.argv[2]
if (!distDir) {
  console.error('Usage: strip-dev-notes.mjs <dist-dir>')
  process.exit(1)
}

const targets = [
  path.join(distDir, 'dev-notes'),
  path.join(distDir, 'dev-notes.md'),
]

let removed = 0
for (const target of targets) {
  if (!fs.existsSync(target)) continue
  fs.rmSync(target, { recursive: true, force: true })
  removed++
  console.log(`  removed ${path.relative(process.cwd(), target)}`)
}

if (removed === 0) {
  console.log(`  no dev-notes/ artifacts found in ${distDir} — nothing to strip`)
} else {
  console.log(`  stripped ${removed} dev-notes artifact(s) from ${distDir}`)
}
