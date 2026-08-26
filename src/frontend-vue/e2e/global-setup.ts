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
  docker(`run -d --name ${APP_NAME} --network ${NETWORK} -p ${APP_HOST_PORT}:8081 ` +
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
