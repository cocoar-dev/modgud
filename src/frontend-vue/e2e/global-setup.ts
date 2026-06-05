import { type FullConfig } from '@playwright/test'
import { execSync } from 'node:child_process'
import path from 'node:path'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Playwright global setup for Modgud E2E.
 *
 * Runs against a **bit-for-bit production image** of the auth API. Nothing
 * is wired in `Development` mode — every timing-sensitive code path the test
 * exercises behaves the way it does in deployed prod. Outbound email is
 * inspected via a real SMTP capture server (Mailpit), not a dev-only
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
 * Image build:
 *   The script auto-builds `modgud:e2e` if it's missing. To force a rebuild
 *   (e.g. after editing backend code), `docker rmi modgud:e2e` between runs.
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
 * Build the publish output and the docker image. Idempotent — the docker layer
 * cache makes the second run nearly free. Mirrors the manual-smoke recipe:
 *   pnpm build → copy dist to wwwroot → dotnet publish → docker build.
 */
function buildImage(): void {
  console.log('[e2e] Building modgud:e2e image (first run, ~30s)...')
  const frontendDir = path.join(REPO_ROOT, 'src', 'frontend-vue')
  const dotnetDir = path.join(REPO_ROOT, 'src', 'dotnet')
  const wwwroot = path.join(dotnetDir, 'Modgud.Api', 'wwwroot')
  const publishOut = path.join(dotnetDir, 'output', 'Modgud')

  // Frontend (skip if dist already exists — dev iteration wins)
  const distDir = path.join(frontendDir, 'dist')
  if (!fs.existsSync(distDir)) {
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
  execSync(`docker build -t ${APP_IMAGE} "${dotnetDir}"`, { stdio: 'inherit' })
  console.log(`[e2e] Image ${APP_IMAGE} built.`)
}

export default async function globalSetup(_config: FullConfig) {
  const externalUrl = process.env.E2E_BASE_URL

  if (externalUrl) {
    console.log(`[e2e] Using external instance: ${externalUrl}`)
    return
  }

  if (!imageExists(APP_IMAGE)) {
    buildImage()
  }

  console.log('[e2e] Starting containers...')

  // Cleanup previous run if still around.
  try { docker(`rm -f ${APP_NAME} ${PG_NAME} ${MAILPIT_NAME}`) } catch { /* ignore */ }
  try { docker(`network rm ${NETWORK}`) } catch { /* ignore */ }

  docker(`network create ${NETWORK}`)

  // PostgreSQL
  docker(`run -d --name ${PG_NAME} --network ${NETWORK} --network-alias postgres ` +
    `-e POSTGRES_DB=modgud_e2e -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres ` +
    `postgres:17-alpine`)
  console.log('[e2e] PostgreSQL starting...')
  await waitFor(() => {
    const logs = docker(`logs ${PG_NAME} 2>&1`)
    return logs.includes('ready to accept connections')
  }, 30_000, 'PostgreSQL ready')

  // Mailpit — captures every outbound mail; read via its HTTP API on 8025.
  // axllent/mailpit defaults: SMTP on 1025, HTTP on 8025, no auth.
  docker(`run -d --name ${MAILPIT_NAME} --network ${NETWORK} --network-alias mailpit ` +
    `-p ${MAILPIT_HTTP_PORT}:8025 ` +
    `axllent/mailpit:latest`)
  console.log('[e2e] Mailpit starting...')
  await waitFor(async () => {
    try {
      const res = await fetch(`http://localhost:${MAILPIT_HTTP_PORT}/api/v1/info`)
      return res.ok
    } catch { return false }
  }, 30_000, 'Mailpit ready')

  // App — Production mode, env vars in the v5 `<section>__<property>` shape.
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
  // The image binds the port from its own `AppUrl=http://0.0.0.0:8081` env var
  // (PascalCase). The previous all-caps `APPURL=…:80` override never took —
  // Cocoar.Configuration reads env vars case-sensitively, so the image's
  // PascalCase value won and the app listened on 8081 while the rig mapped :80,
  // so /api/health was never reachable. Map the port the app actually binds.
  docker(`run -d --name ${APP_NAME} --network ${NETWORK} -p ${APP_HOST_PORT}:8081 ` +
    `-e ASPNETCORE_ENVIRONMENT=Staging ` +
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
    `${APP_IMAGE}`)

  const baseURL = `http://localhost:${APP_HOST_PORT}`
  console.log(`[e2e] App starting, polling ${baseURL}/api/health ...`)
  await waitFor(async () => {
    try {
      const res = await fetch(`${baseURL}/api/health`)
      return res.ok
    } catch { return false }
  }, 60_000, 'App healthy')

  // Post-cutover (C15 reform) there is no anonymous /setup endpoint to
  // mint the first admin — the recovery CLI is the supported path.
  // Exec into the live container, run the bootstrap-admin command, and
  // proceed once the credentials are in place. Idempotent on a fresh
  // DB; harmless if re-run with a different password (creates a new
  // admin user with a unique username).
  console.log(`[e2e] Bootstrapping first admin via recovery CLI ...`)
  try {
    docker(`exec ${APP_NAME} dotnet Modgud.Api.dll recover bootstrap-admin ` +
      `--email ${E2E_ADMIN_EMAIL} --username ${E2E_ADMIN_USER} ` +
      `--firstname E2E --lastname Admin --password "${E2E_ADMIN_PASSWORD}"`)
  } catch (err) {
    throw new Error(
      `[e2e] bootstrap-admin failed. The CLI is invoked inside the app ` +
      `container; check 'docker logs ${APP_NAME}' for the underlying error. ${err}`)
  }

  // Persist state so specs and teardown can find the rig.
  fs.writeFileSync(STATE_FILE, JSON.stringify({
    baseURL,
    mailpitUrl: `http://localhost:${MAILPIT_HTTP_PORT}`,
    network: NETWORK,
    containers: { app: APP_NAME, pg: PG_NAME, mailpit: MAILPIT_NAME },
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
