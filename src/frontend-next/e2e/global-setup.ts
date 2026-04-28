import { request, type FullConfig } from '@playwright/test'
import { execSync } from 'node:child_process'
import path from 'node:path'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const STATE_FILE = path.join(__dirname, '.e2e-containers.json')
const NETWORK = 'timetodo-e2e-net'
const PG_NAME = 'timetodo-e2e-pg'
const APP_NAME = 'timetodo-e2e-app'
const APP_IMAGE = 'timetodo:e2e'
const APP_HOST_PORT = 19090 // fixed port to avoid conflicts
const TESTIDP_NAME = 'timetodo-e2e-testidp'
const TESTIDP_IMAGE = 'timetodo-testidp:e2e'
const TESTIDP_HOST_PORT = 15000 // fixed — matches TESTIDP_ISSUER

function docker(cmd: string): string {
  return execSync(`docker ${cmd}`, { encoding: 'utf-8' }).trim()
}

/**
 * Playwright global setup.
 *
 * Modes:
 *   - E2E_BASE_URL set  → use external instance (local dev server, staging, etc.)
 *   - E2E_BASE_URL unset → start PostgreSQL + App via Docker
 *
 * Prerequisites for Docker mode:
 *   docker build -f docker/Dockerfile -t timetodo:e2e .
 */
export default async function globalSetup(config: FullConfig) {
  const externalUrl = process.env.E2E_BASE_URL

  if (externalUrl) {
    console.log(`[e2e] Using external instance: ${externalUrl}`)
    await seedAdminIfNeeded(externalUrl)
    return
  }

  console.log('[e2e] Starting containers...')

  // Cleanup previous run if still around
  try { docker(`rm -f ${APP_NAME} ${PG_NAME} ${TESTIDP_NAME}`) } catch { /* ignore */ }
  try { docker(`network rm ${NETWORK}`) } catch { /* ignore */ }

  // Network
  docker(`network create ${NETWORK}`)

  // PostgreSQL
  docker(`run -d --name ${PG_NAME} --network ${NETWORK} --network-alias postgres \
    -e POSTGRES_DB=timetodo_e2e -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
    postgres:17`)

  console.log('[e2e] PostgreSQL starting...')
  await waitFor(() => {
    const logs = docker(`logs ${PG_NAME} 2>&1`)
    return logs.includes('ready to accept connections')
  }, 30_000, 'PostgreSQL ready')

  // TestIdP (OpenIddict mock). Published on host port 15000 so the browser can
  // reach it at http://host.docker.internal:15000/ on Docker Desktop. The App
  // container uses the same URL via --add-host host.docker.internal:host-gateway,
  // so discovery/issuer/redirect URLs match from both sides.
  //
  // Prereq: docker build -f docker/Dockerfile.testidp -t timetodo-testidp:e2e .
  const testIdpIssuer = `http://host.docker.internal:${TESTIDP_HOST_PORT}/`
  docker(`run -d --name ${TESTIDP_NAME} --network ${NETWORK} --network-alias testidp \
    -p ${TESTIDP_HOST_PORT}:5000 \
    -e TESTIDP_ISSUER=${testIdpIssuer} \
    ${TESTIDP_IMAGE}`)

  console.log('[e2e] TestIdP starting...')
  await waitFor(async () => {
    try {
      const res = await fetch(`${testIdpIssuer}.well-known/openid-configuration`)
      return res.ok
    } catch { return false }
  }, 60_000, 'TestIdP ready')

  // App. PublicUrl matches the test host so FIDO2/WebAuthn accepts the relying
  // party ID — otherwise passkey registration rejects with "not a registrable
  // domain suffix of the current domain".
  //
  // Email is forced to Smtp with a local host so the InMemoryEmailService (dev-only
  // wrapper) captures outbound mail for the /api/dev/emails endpoint. Without this
  // the bundled data/configuration.local.json would pick up a real Postmark token,
  // which rejects the test recipient as "inactive" after prior CI failures.
  const connStr = 'Host=postgres;Port=5432;Database=timetodo_e2e;Username=postgres;Password=postgres'
  const publicUrl = `http://localhost:${APP_HOST_PORT}`
  // --add-host host.docker.internal:host-gateway lets the app resolve the
  // TestIdP URL on Linux the same way Docker Desktop auto-injects it on
  // Windows/Mac. Without this, OIDC discovery fails on CI.
  docker(`run -d --name ${APP_NAME} --network ${NETWORK} -p ${APP_HOST_PORT}:8081 \
    --add-host host.docker.internal:host-gateway \
    -e ASPNETCORE_ENVIRONMENT=Development \
    -e AppUrl=http://0.0.0.0:8081 \
    -e PublicUrl=${publicUrl} \
    -e CertPath= -e CertPassword= \
    -e "DbSettings__ConnectionString=${connStr}" \
    -e AppSettings__AuthenticationMinimumLevel=0 \
    -e Email__Provider=Smtp \
    -e Email__Smtp__Host=127.0.0.1 \
    -e Email__Smtp__Port=2525 \
    -e Email__Smtp__FromAddress=noreply@timetodo.e2e \
    -e MagicLink__RateLimitMinutes=0 \
    -e EmailOtp__RateLimitMinutes=0 \
    ${APP_IMAGE}`)

  const baseURL = `http://localhost:${APP_HOST_PORT}`
  console.log(`[e2e] App starting, waiting for ${baseURL}/api/health ...`)

  // Poll health
  await waitFor(async () => {
    try {
      const res = await fetch(`${baseURL}/api/health`)
      return res.ok
    } catch { return false }
  }, 120_000, 'App healthy')

  console.log(`[e2e] App ready on ${baseURL}`)

  // Persist state — includes TestIdP metadata so specs can reach it
  // without re-introspecting containers.
  fs.writeFileSync(STATE_FILE, JSON.stringify({
    baseURL,
    testIdpIssuer,
    testIdpClientId: 'timetodo-e2e',
    testIdpClientSecret: 'e2e-secret',
  }))
  process.env.E2E_BASE_URL = baseURL
  process.env.E2E_TESTIDP_ISSUER = testIdpIssuer
  process.env.E2E_TESTIDP_CLIENT_ID = 'timetodo-e2e'
  process.env.E2E_TESTIDP_CLIENT_SECRET = 'e2e-secret'

  // Seed admin
  await seedAdminIfNeeded(baseURL)
}

async function seedAdminIfNeeded(baseURL: string) {
  const api = await request.newContext({ baseURL })

  try {
    const statusRes = await api.get('/api/setup/status')
    const status = await statusRes.json()

    if (status.NeedsSetup) {
      console.log('[e2e] Creating test admin "ka"')
      const createRes = await api.post('/api/setup/create-admin', {
        data: {
          UserName: 'ka',
          Password: 'Test1234!',
          Firstname: 'Test',
          Lastname: 'Admin',
          Email: 'ka@test.com',
        },
      })
      if (!createRes.ok()) {
        throw new Error(`Failed to create admin: ${createRes.status()} ${await createRes.text()}`)
      }
      await api.post('/api/account/logout')
    }

    try {
      await api.post('/api/dev/reset-mfa/ka')
      await api.delete('/api/dev/emails')
    } catch { /* dev endpoints may not be available (Release build) */ }

    console.log('[e2e] Clean state ready')
  } finally {
    await api.dispose()
  }
}

async function waitFor(
  check: () => boolean | Promise<boolean>,
  timeoutMs: number,
  label: string,
) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (await check()) return
    await new Promise(r => setTimeout(r, 2_000))
  }
  throw new Error(`[e2e] Timeout waiting for: ${label}`)
}
