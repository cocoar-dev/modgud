import { execSync } from 'node:child_process'
import path from 'node:path'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'
import { RUN_LABEL, processAlive } from './global-setup'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const STATE_FILE = path.join(__dirname, '.e2e-containers.json')

function docker(cmd: string): string {
  return execSync(`docker ${cmd}`, { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'pipe'] }).trim()
}

function ids(cmd: string): string[] {
  return docker(cmd).split(/\s+/).filter(Boolean)
}

/**
 * Playwright global teardown — removes the containers and network of THIS run
 * (B10-05): selected by the run label, never by name alone. The state file is
 * deleted only after the cleanup succeeded; a failed cleanup keeps it and fails
 * the teardown, so the leftovers stay visible and the next run can retry.
 */
export default async function globalTeardown() {
  if (!fs.existsSync(STATE_FILE)) {
    console.log('[e2e teardown] No state file — nothing to clean up')
    return
  }

  const state = JSON.parse(fs.readFileSync(STATE_FILE, 'utf-8')) as { runId?: string, pid?: number }
  if (!state.runId) {
    console.warn('[e2e teardown] State file without a run id (older rig) — leaving it and any containers alone')
    return
  }
  if (state.pid && state.pid !== process.pid && processAlive(state.pid)) {
    console.log(`[e2e teardown] The rig belongs to the live run ${state.runId} — not touching it`)
    return
  }

  const filter = `--filter label=${RUN_LABEL}=${state.runId}`
  const failures: string[] = []
  console.log(`[e2e teardown] Removing the containers of run ${state.runId}...`)
  try {
    const containers = ids(`ps -aq ${filter}`)
    if (containers.length > 0) docker(`rm -f ${containers.join(' ')}`)
  } catch (e) { failures.push(`containers: ${e}`) }
  try {
    const networks = ids(`network ls -q ${filter}`)
    if (networks.length > 0) docker(`network rm ${networks.join(' ')}`)
  } catch (e) { failures.push(`network: ${e}`) }

  let left: string[] = []
  try {
    left = [...ids(`ps -aq ${filter}`), ...ids(`network ls -q ${filter}`)]
  } catch (e) { failures.push(`verify: ${e}`) }

  if (failures.length > 0 || left.length > 0) {
    throw new Error(`[e2e teardown] Cleanup of run ${state.runId} incomplete; state file kept. ` +
      `${failures.join('; ')}${left.length > 0 ? ` Still present: ${left.join(', ')}` : ''}`)
  }

  fs.unlinkSync(STATE_FILE)
  console.log('[e2e teardown] Done')
}
