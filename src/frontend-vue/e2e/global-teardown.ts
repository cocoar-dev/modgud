import { execSync } from 'node:child_process'
import path from 'node:path'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const STATE_FILE = path.join(__dirname, '.e2e-containers.json')

function docker(cmd: string) {
  try { execSync(`docker ${cmd}`, { encoding: 'utf-8', stdio: 'pipe' }) } catch { /* ignore */ }
}

/**
 * Playwright global teardown — stops and removes the test containers + network.
 */
export default async function globalTeardown() {
  if (!fs.existsSync(STATE_FILE)) {
    console.log('[e2e teardown] No state file — nothing to clean up')
    return
  }

  fs.unlinkSync(STATE_FILE)

  console.log('[e2e teardown] Stopping containers...')
  docker('rm -f modgud-e2e-app modgud-e2e-pg modgud-e2e-mailpit')
  docker('network rm modgud-e2e-net')
  console.log('[e2e teardown] Done')
}
