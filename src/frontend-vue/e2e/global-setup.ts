import { type FullConfig } from '@playwright/test'
import { execSync } from 'node:child_process'
import path from 'node:path'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Playwright global setup for Modgud E2E.
 *
 * Runs against a **Release image built from the current working tree**, in the
 * `Staging` environment (production build and behaviour, not Development). It is
 * NOT a production-equivalent deployment (B10-06): the rig uses ephemeral
 * OpenIddict signing keys (`OPENIDDICT__DEVELOPMENTMODE=true`), a localhost
 * issuer, and switches the magic-link / e-mail-OTP rate limits off, so the
 * production-only start guards and real rate limits are not exercised. Outbound
 * email is inspected via a real SMTP capture server (Mailpit), not a dev-only
 * inspection endpoint, so the SmtpEmailService path is tested too.
 *
 * Topology:
 *
 *   ┌──────────────┐   port 1025 SMTP    ┌──────────────┐
 *   │ modgud  │────────────────────▶│ mailpit       │  exposes
 *   │ Production   │                     │  - SMTP 1025  │  - HTTP API on 8025
 *   │ talks to     │                     │  - Web UI 8025│    (read mails from tests)
 *   │ postgres +   │                     └──────────────┘
 *   │ mailpit      │
 *   └──────┬───────┘
 *          │ Marten 5432
 *          ▼
 *   ┌──────────────┐
 *   │ postgres     │
 *   └──────────────┘
 *
 * Modes:
 *   - E2E_BASE_URL set  → use external instance (skip docker management).
 *   - E2E_BASE_URL unset → bring up the rig via raw `docker run` commands.
 *
 * Image build (B10-06):
 *   `modgud:e2e` carries the git commit it was built from. It is rebuilt when
 *   that commit differs from HEAD or the working tree has changes, and the
 *   frontend `dist` is rebuilt too. Set E2E_REUSE_IMAGE=1 / E2E_REUSE_DIST=1 to
 *   reuse an existing image / dist anyway while iterating on specs only.
 *
 * Ownership (B10-05):
 *   Every container and the network carry the label `modgud.e2e.run=<run id>`.
 *   The state file is written BEFORE anything starts and names the run and its
 *   process; another live run's rig is never removed, and a container with one
 *   of the rig's names but without the label is never touched.
 */

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const STATE_FILE = path.join(__dirname, '.e2e-containers.json')

const NETWORK = 'modgud-e2e-net'
const PG_NAME = 'modgud-e2e-pg'
const APP_NAME = 'modgud-e2e-app'
const APP_IMAGE = 'modgud:e2e'
const APP_HOST_PORT = 14200 // bound port avoids collisions with manual-smoke 4200
const MAILPIT_NAME = 'modgud-e2e-mailpit'
const MAILPIT_HTTP_PORT = 18025

// Bootstrap-admin credentials. Override via env vars so a CI matrix
// can vary them per realm or per branch; specs read the same env vars
// (`process.env.E2E_ADMIN_USER` / `E2E_ADMIN_PASSWORD`) so seed and
// login stay in lockstep.
const E2E_ADMIN_USER = process.env.E2E_ADMIN_USER ?? 'admin'
const E2E_ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'admin@modgud.test'
const E2E_ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'ABC12abc!'

// Project root: walk up from src/frontend-vue/e2e/ to repo root.
const REPO_ROOT = path.resolve(__dirname, '..', '..', '..')

export const RUN_LABEL = 'modgud.e2e.run'
const SOURCE_LABEL = 'modgud.e2e.source'

/** True when a process with this id is alive (signal 0 only probes). */
export function processAlive(pid: number): boolean {
  try { process.kill(pid, 0); return true } catch (e) { return (e as NodeJS.ErrnoException).code === 'EPERM' }
}

/** The tree the image would be built from: HEAD plus a marker for local changes. */
function sourceStamp(): string {
  const head = execSync('git rev-parse HEAD', { cwd: REPO_ROOT, encoding: 'utf-8' }).trim()
  const dirty = execSync('git status --porcelain --untracked-files=no', { cwd: REPO_ROOT, encoding: 'utf-8' }).trim()
  return dirty.length > 0 ? `${head}-dirty` : head
}

function docker(cmd: string): string {
  return execSync(`docker ${cmd}`, { encoding: 'utf-8' }).trim()
}

function dockerSilent(cmd: string): string {
  return execSync(`docker ${cmd}`, { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'pipe'] }).trim()
}

function imageExists(tag: string): boolean {
  try {
    const id = dockerSilent(`image inspect ${tag} --format "{{.Id}}"`)
    return id.length > 0
  } catch {
    return false
  }
}

/**
 * A docker object's labels, or null when it does not exist. Read as JSON: an
 * `index .Labels "name"` template needs quotes inside the shell's quotes, and sh
 * ended the string there, so every lookup failed as "does not exist" — leftovers
 * were never removed and the image was always rebuilt.
 */
function labelsOf(kind: 'image' | 'container' | 'network', name: string): Record<string, string> | null {
  const field = kind === 'network' ? '.Labels' : '.Config.Labels'
  let raw: string
  try { raw = dockerSilent(`${kind} inspect ${name} --format "{{json ${field}}}"`) } catch { return null }
  return (JSON.parse(raw) as Record<string, string> | null) ?? {}
}

/** B10-06 — reuse an image only when it was built from exactly this clean commit. */
function imageMatchesSource(tag: string, stamp: string): boolean {
  if (stamp.endsWith('-dirty')) return false
  return labelsOf('image', tag)?.[SOURCE_LABEL] === stamp
}

/**
 * Build the publish output and the docker image. Idempotent — the docker layer
 * cache makes the second run nearly free. Mirrors the manual-smoke recipe:
 *   pnpm build → copy dist to wwwroot → dotnet publish → docker build.
 */
function buildImage(stamp: string): void {
  console.log('[e2e] Building modgud:e2e image (first run, ~30s)...')
  const frontendDir = path.join(REPO_ROOT, 'src', 'frontend-vue')
  const dotnetDir = path.join(REPO_ROOT, 'src', 'dotnet')
  const wwwroot = path.join(dotnetDir, 'Modgud.Api', 'wwwroot')
  const publishOut = path.join(dotnetDir, 'output', 'Modgud')

  // Frontend — always rebuilt from the tree, unless E2E_REUSE_DIST=1 asks to reuse dist.
  const distDir = path.join(frontendDir, 'dist')
  if (process.env.E2E_REUSE_DIST !== '1' || !fs.existsSync(distDir)) {
    execSync('pnpm build', { cwd: frontendDir, stdio: 'inherit' })
  }

  // Stage frontend into wwwroot. fs.cpSync (not `cp -r`) so the build works on
  // Windows dev machines too, not just Unix CI.
  fs.rmSync(wwwroot, { recursive: true, force: true })
  fs.mkdirSync(wwwroot, { recursive: true })
  fs.cpSync(distDir, wwwroot, { recursive: true })

  // Backend publish
  fs.rmSync(publishOut, { recursive: true, force: true })
  execSync(
    `dotnet publish Modgud.Api/Modgud.Api.csproj -c Release -o "${publishOut}" --nologo`,
    { cwd: dotnetDir, stdio: 'inherit' },
  )

  // Docker
  execSync(`docker build -t ${APP_IMAGE} --label ${SOURCE_LABEL}=${stamp} "${dotnetDir}"`, { stdio: 'inherit' })
  console.log(`[e2e] Image ${APP_IMAGE} built.`)
}

export default async function globalSetup(_config: FullConfig) {
  const externalUrl = process.env.E2E_BASE_URL

  if (externalUrl) {
    console.log(`[e2e] Using external instance: ${externalUrl}`)
    return
  }

  // B10-05 — never take over a rig another live run still owns.
  if (fs.existsSync(STATE_FILE)) {
    const previous = JSON.parse(fs.readFileSync(STATE_FILE, 'utf-8')) as { runId?: string, pid?: number }
    if (previous.pid && previous.pid !== process.pid && processAlive(previous.pid)) {
      throw new Error(`[e2e] Another e2e run (pid ${previous.pid}, run ${previous.runId}) owns the rig. ` +
        'Wait for it to finish; two runs cannot share the fixed container names and ports.')
    }
  }

  const stamp = sourceStamp()
  if (process.env.E2E_REUSE_IMAGE === '1' && imageExists(APP_IMAGE)) {
    console.log(`[e2e] E2E_REUSE_IMAGE=1 — reusing ${APP_IMAGE} without checking its source`)
  } else if (!imageMatchesSource(APP_IMAGE, stamp)) {
    buildImage(stamp)
  }

  console.log('[e2e] Starting containers...')

  // Leftovers of an earlier, finished run: only what carries the rig label.
  for (const name of [APP_NAME, PG_NAME, MAILPIT_NAME]) {
    const labels = labelsOf('container', name)
    if (labels === null) continue
    if (!labels[RUN_LABEL]) throw new Error(`[e2e] A container named ${name} exists but was not created by this rig; refusing to remove it.`)
    docker(`rm -f ${name}`)
  }
  const netLabels = labelsOf('network', NETWORK)
  if (netLabels !== null) {
    if (!netLabels[RUN_LABEL]) throw new Error(`[e2e] A network named ${NETWORK} exists but was not created by this rig; refusing to remove it.`)
    docker(`network rm ${NETWORK}`)
  }

  // Ownership first: the state file exists before any resource does, so a failed
  // setup still leaves teardown the run id to clean up by.
  const runId = `${process.pid}-${Date.now()}`
  const runLabel = `--label ${RUN_LABEL}=${runId}`
  const state: Record<string, unknown> = {
    runId, pid: process.pid, ready: false,
    network: NETWORK,
    containers: { app: APP_NAME, pg: PG_NAME, mailpit: MAILPIT_NAME },
  }
  fs.writeFileSync(STATE_FILE, JSON.stringify(state))

  docker(`network create ${runLabel} ${NETWORK}`)

  // PostgreSQL
  docker(`run -d --name ${PG_NAME} ${runLabel} --network ${NETWORK} --network-alias postgres ` +
    `-e POSTGRES_DB=modgud_e2e -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres ` +
    `postgres:17-alpine`)
  console.log('[e2e] PostgreSQL starting...')
  await waitFor(() => {
    const logs = docker(`logs ${PG_NAME} 2>&1`)
    return logs.includes('ready to accept connections')
  }, 30_000, 'PostgreSQL ready')

  // Mailpit — captures every outbound mail; read via its HTTP API on 8025.
  // axllent/mailpit defaults: SMTP on 1025, HTTP on 8025, no auth.
  docker(`run -d --name ${MAILPIT_NAME} ${runLabel} --network ${NETWORK} --network-alias mailpit ` +
    `-p ${MAILPIT_HTTP_PORT}:8025 ` +
    `axllent/mailpit:latest`)
  console.log('[e2e] Mailpit starting...')
  await waitFor(async () => {
    try {
      const res = await fetch(`http://localhost:${MAILPIT_HTTP_PORT}/api/v1/info`)
      return res.ok
    } catch { return false }
  }, 30_000, 'Mailpit ready')

  // App — Production mode, env vars in the `<section>__<property>` shape.
  // SMTP points at mailpit on the shared network; mails land in mailpit's
  // store and become readable via http://localhost:18025/api/v1/messages.
  // OPENIDDICT__DEVELOPMENTMODE=true keeps signing keys ephemeral so we
  // don't need a real cert in the test image.
  const connStr = `Host=postgres;Database=modgud_e2e;Username=postgres;Password=postgres;Keepalive=30`
  const publicUrl = `http://localhost:${APP_HOST_PORT}`
  // Environment = Staging, NOT Production. The rig deliberately runs with
  // ephemeral OpenIddict signing keys (OPENIDDICT__DEVELOPMENTMODE=true) and a
  // localhost issuer so it needs no real certificate or public hostname — but
  // the app's hard production guards (Program.cs, gated on IsProduction())
  // reject exactly that combination, so a Production container crashes on boot
  // ("DevelopmentMode must be false in Production"). Staging uses the same
  // production build + production behaviour (it is NOT Development) while
  // skipping those deploy-only guards, so the rig can actually start.
  // Bind port: pin it explicitly with the `AppUrl` env var (PascalCase!) and
  // map that port. The freshly-published image ships NO data/configuration.json
  // (the thin Dockerfile COPYs the publish output, and `dotnet publish` does not
  // emit data/ there), so StartUpConfiguration.AppUrl falls back to its class
  // default `http://0.0.0.0:80` and the app binds :80 — not :8081. We do not
  // rely on that default: `app.Run(conf.AppUrl)` lets config override the bind.
  // (Cocoar.Configuration v6 binds env vars case-insensitively, so `AppUrl` and
  // `APPURL` are equivalent — we use PascalCase here for readability.) Pin :8081
  // and map the same port so /api/health is reachable regardless of the image's
  // compiled-in default. A previously-tagged stand-in
  // image happened to bake AppUrl=:8081, which masked this — a fresh build does
  // not, and binds :80.
  docker(`run -d --name ${APP_NAME} ${runLabel} --network ${NETWORK} -p ${APP_HOST_PORT}:8081 ` +
    `-e ASPNETCORE_ENVIRONMENT=Staging ` +
    `-e AppUrl=http://0.0.0.0:8081 ` +
    `-e PUBLICURL=${publicUrl} ` +
    `-e DBSETTINGS__CONNECTIONSTRING="${connStr}" ` +
    `-e OPENIDDICT__ISSUER=${publicUrl} ` +
    `-e OPENIDDICT__DEVELOPMENTMODE=true ` +
    `-e EMAIL__PROVIDER=Smtp ` +
    `-e EMAIL__SMTP__HOST=mailpit ` +
    `-e EMAIL__SMTP__PORT=1025 ` +
    `-e EMAIL__SMTP__USESSL=false ` +
    `-e EMAIL__SMTP__FROMADDRESS=noreply@modgud.test ` +
    `-e EMAIL__SMTP__FROMNAME="Modgud E2E" ` +
    `-e MAGICLINK__RATELIMITMINUTES=0 ` +
    `-e EMAILOTP__RATELIMITMINUTES=0 ` +
    `-e AppSettings__Features__PositionTerminals=true ` +
    `${APP_IMAGE}`)

  const baseURL = `http://localhost:${APP_HOST_PORT}`
  console.log(`[e2e] App starting, polling ${baseURL}/health/ready ...`)
  await waitFor(async () => {
    try {
      const res = await fetch(`${baseURL}/health/ready`)
      return res.ok
    } catch { return false }
  }, 60_000, 'App healthy')

  // Post-cutover (C15 reform) a zero-realm deployment must be installed with
  // an operator-issued, single-use token. Exercise the same contract as
  // production: issue the token inside the container, then complete the
  // installation through the public HTTP API.
  console.log(`[e2e] Installing first realm via recovery token ...`)
  try {
    const issued = docker(`exec ${APP_NAME} dotnet Modgud.Api.dll recover install-link ` +
      `--base-url ${baseURL} --minutes 10 --json`)
    const jsonLine = issued.split(/\r?\n/).filter(Boolean).at(-1)
    const token = jsonLine ? JSON.parse(jsonLine).token as string | undefined : undefined
    if (!token) throw new Error('install-link returned no token')

    const complete = await fetch(`${baseURL}/api/install/complete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        Token: token,
        Realm: {
          Slug: 'e2e',
          DisplayName: 'Modgud E2E',
          Description: 'Isolated Playwright test realm',
          Domains: ['localhost'],
          PrimaryDomain: 'localhost',
        },
        Admin: {
          UserName: E2E_ADMIN_USER,
          Email: E2E_ADMIN_EMAIL,
          Firstname: 'E2E',
          Lastname: 'Admin',
          Password: E2E_ADMIN_PASSWORD,
        },
      }),
    })
    if (!complete.ok) {
      throw new Error(`installation returned ${complete.status}: ${await complete.text()}`)
    }
  } catch (err) {
    throw new Error(
      `[e2e] first installation failed. Check 'docker logs ${APP_NAME}' ` +
      `for the underlying error. ${err}`)
  }

  // Persist state so specs and teardown can find the rig.
  fs.writeFileSync(STATE_FILE, JSON.stringify({
    ...state,
    ready: true,
    baseURL,
    mailpitUrl: `http://localhost:${MAILPIT_HTTP_PORT}`,
  }))
  process.env.E2E_BASE_URL = baseURL
  process.env.E2E_MAILPIT_URL = `http://localhost:${MAILPIT_HTTP_PORT}`

  console.log(`[e2e] App ready on ${baseURL}, mailpit on http://localhost:${MAILPIT_HTTP_PORT}`)
}

async function waitFor(
  check: () => boolean | Promise<boolean>,
  timeoutMs: number,
  label: string,
) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (await check()) return
    await new Promise(r => setTimeout(r, 1_000))
  }
  throw new Error(`[e2e] Timeout waiting for: ${label}`)
}
