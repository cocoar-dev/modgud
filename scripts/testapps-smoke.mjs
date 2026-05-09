#!/usr/bin/env node
/**
 * End-to-end smoke test of the Cocoar.Auth permission model.
 *
 * Drives a wide grid of (user, client) combinations through the full
 * OAuth flow (auth-code + PKCE for SPAs, client_credentials for M2M),
 * then for each token:
 *   1. Decodes + prints the access-token claims (sub, aud, scope, …)
 *   2. Calls /connect/userinfo and prints the resource_access shape
 *   3. (optional, when TESTAPPS_RESOURCEAPI_URL is set) calls
 *      ResourceApi /me with the token and prints what the lib
 *      flattened onto the principal
 *
 * The grid covers different App shapes (single-RS WordPress-style,
 * multi-microservice e-commerce, mini single-service CRM) and
 * different user role mixes (direct grants, resource-admin pre-
 * expansion, realm-admin, cross-app, pure-auth).
 *
 * Output: prints to stdout AND writes a structured markdown report to
 * .local/testapps-smoke-results.md so the user can review it after.
 *
 * Prerequisites:
 *   - IdP running on $BASE_URL (default http://localhost:9099)
 *   - Demo data seeded (`node scripts/seed-demo.mjs`)
 *   - Optional: TestApps.ResourceApi running on $TESTAPPS_RESOURCEAPI_URL
 *     for the RS-side echo (default: skipped if env var not set)
 *
 * Usage:
 *   node scripts/testapps-smoke.mjs
 *
 *   BASE_URL=http://localhost:9099 \
 *   TESTAPPS_RESOURCEAPI_URL=http://localhost:7081 \
 *   node scripts/testapps-smoke.mjs
 */

import { createHash, randomBytes } from 'node:crypto'
import { writeFileSync, mkdirSync } from 'node:fs'
import { resolve as resolvePath, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

// ── config ────────────────────────────────────────────────────────────────

const HERE = dirname(fileURLToPath(import.meta.url))
const REPO_ROOT = resolvePath(HERE, '..')
const REPORT_PATH = resolvePath(REPO_ROOT, '.local/testapps-smoke-results.md')

const BASE_URL              = process.env.BASE_URL              ?? 'http://localhost:9099'
const TESTAPPS_RESOURCEAPI  = process.env.TESTAPPS_RESOURCEAPI_URL ?? null
const PASSWORD              = process.env.SMOKE_PASSWORD        ?? 'Demo1234!'
const REDIRECT_URI          = 'http://localhost/test-callback'

// ── tiny http client with per-user cookie jars ────────────────────────────

class CookieJar {
  constructor() { this.cookies = new Map() }
  setFromHeader(setCookie) {
    if (!setCookie) return
    for (const part of Array.isArray(setCookie) ? setCookie : [setCookie]) {
      const seg = part.split(';')[0].trim()
      const eq = seg.indexOf('=')
      if (eq > 0) this.cookies.set(seg.slice(0, eq), seg.slice(eq + 1))
    }
  }
  header() {
    return [...this.cookies.entries()].map(([k, v]) => `${k}=${v}`).join('; ')
  }
}

async function httpRequest(method, url, { headers = {}, body = null, jar = null, redirect = 'follow' } = {}) {
  const h = { ...headers }
  if (jar) {
    const cookie = jar.header()
    if (cookie) h.Cookie = cookie
  }
  if (body && typeof body === 'object' && !(body instanceof URLSearchParams)) {
    h['Content-Type'] = 'application/json'
    body = JSON.stringify(body)
  }
  const res = await fetch(url, { method, headers: h, body, redirect })
  if (jar) jar.setFromHeader(res.headers.getSetCookie?.() ?? res.headers.get('set-cookie'))
  const text = await res.text()
  let parsed = null
  if (text) { try { parsed = JSON.parse(text) } catch { parsed = text } }
  return { ok: res.ok, status: res.status, headers: res.headers, body: parsed, raw: text }
}

// ── auth-code + PKCE flow helpers ─────────────────────────────────────────

function base64url(buf) {
  return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}
function pkcePair() {
  const verifier = base64url(randomBytes(32))
  const challenge = base64url(createHash('sha256').update(verifier).digest())
  return { verifier, challenge }
}

function decodeJwt(token) {
  const parts = token.split('.')
  if (parts.length < 2) return { error: 'not-a-jwt' }
  const b64 = parts[1].replace(/-/g, '+').replace(/_/g, '/')
  const padded = b64 + '='.repeat((4 - b64.length % 4) % 4)
  try { return JSON.parse(Buffer.from(padded, 'base64').toString('utf8')) }
  catch (e) { return { error: 'decode-failed: ' + e.message } }
}

async function loginAsUser(userName, password = PASSWORD) {
  const jar = new CookieJar()
  const res = await httpRequest('POST', `${BASE_URL}/api/account/login`,
    { jar, body: { UserName: userName, Password: password } })
  if (!res.ok) throw new Error(`Login as '${userName}' failed: HTTP ${res.status} — ${JSON.stringify(res.body)}`)
  return jar
}

/**
 * Drives the OAuth auth-code+PKCE flow as `userName`. Returns
 * { accessToken, idToken, scope } on success; throws on any step
 * failure with a clear message.
 */
async function driveAuthCode({ userName, clientId, clientSecret, scopes, resources = [] }) {
  const jar = await loginAsUser(userName)

  const { verifier, challenge } = pkcePair()
  const state = base64url(randomBytes(8))
  const params = new URLSearchParams({
    response_type: 'code',
    client_id: clientId,
    redirect_uri: REDIRECT_URI,
    scope: scopes.join(' '),
    state,
    code_challenge: challenge,
    code_challenge_method: 'S256',
  })
  for (const r of resources) params.append('resource', r)

  const authResp = await httpRequest('GET', `${BASE_URL}/connect/authorize?${params}`,
    { jar, redirect: 'manual' })
  if (![301, 302, 303, 307, 308].includes(authResp.status)) {
    throw new Error(`/connect/authorize did not redirect (got ${authResp.status}): ${authResp.raw?.slice(0, 400)}`)
  }
  const location = authResp.headers.get('location')
  const queryStart = location.indexOf('?')
  const fragmentStart = location.indexOf('#')
  const qs = queryStart >= 0
    ? new URLSearchParams(location.slice(queryStart + 1, fragmentStart < 0 ? undefined : fragmentStart))
    : new URLSearchParams()
  const code = qs.get('code')
  if (!code) throw new Error(`No 'code' in authorize redirect. Location: ${location}`)

  const tokenForm = new URLSearchParams({
    grant_type: 'authorization_code',
    code,
    client_id: clientId,
    redirect_uri: REDIRECT_URI,
    code_verifier: verifier,
  })
  if (clientSecret) tokenForm.set('client_secret', clientSecret)
  for (const r of resources) tokenForm.append('resource', r)

  const tokenResp = await httpRequest('POST', `${BASE_URL}/connect/token`,
    { headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: tokenForm.toString() })
  if (!tokenResp.ok) throw new Error(`/connect/token failed (${tokenResp.status}): ${tokenResp.raw?.slice(0, 400)}`)

  return {
    accessToken: tokenResp.body.access_token,
    idToken: tokenResp.body.id_token,
    scope: tokenResp.body.scope,
  }
}

async function driveClientCredentials({ clientId, clientSecret, scopes }) {
  const tokenForm = new URLSearchParams({
    grant_type: 'client_credentials',
    client_id: clientId,
    client_secret: clientSecret,
    scope: scopes.join(' '),
  })
  const tokenResp = await httpRequest('POST', `${BASE_URL}/connect/token`,
    { headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: tokenForm.toString() })
  if (!tokenResp.ok) throw new Error(`/connect/token (M2M) failed (${tokenResp.status}): ${tokenResp.raw?.slice(0, 400)}`)
  return { accessToken: tokenResp.body.access_token, scope: tokenResp.body.scope }
}

async function fetchUserInfo(accessToken) {
  const res = await httpRequest('GET', `${BASE_URL}/connect/userinfo`,
    { headers: { Authorization: `Bearer ${accessToken}` } })
  return { ok: res.ok, status: res.status, body: res.body }
}

async function fetchTestAppsMe(accessToken) {
  if (!TESTAPPS_RESOURCEAPI) return null
  const res = await httpRequest('GET', `${TESTAPPS_RESOURCEAPI}/me`,
    { headers: { Authorization: `Bearer ${accessToken}` } })
  return { ok: res.ok, status: res.status, body: res.body }
}

// ── scenarios ─────────────────────────────────────────────────────────────

const CLIENT_SECRETS = {
  'alpha-blog-spa':       'alpha-blog-spa-secret-please-rotate',
  'beta-shop-spa':        'beta-shop-spa-secret-please-rotate',
  'gamma-crm-mini-ui':    'gamma-crm-mini-ui-secret-please-rotate',
  'cross-app-dashboard':  'cross-app-dashboard-secret-please-rotate',
  'smoke-m2m-backend':    'smoke-m2m-backend-secret-please-rotate',
}

const CLIENT_SCOPES = {
  'alpha-blog-spa':       ['openid', 'roles', 'alpha-blog.use'],
  'beta-shop-spa':        ['openid', 'roles', 'beta-orders.use', 'beta-products.use', 'beta-shipping.use'],
  'gamma-crm-mini-ui':    ['openid', 'roles', 'gamma-crm.use'],
  'cross-app-dashboard':  ['openid', 'roles', 'alpha-blog.use', 'beta-orders.use', 'gamma-crm.use'],
  'smoke-m2m-backend':    ['beta-orders.use'],
}

const SCENARIOS = [
  { name: 'Alice on alpha-blog-spa',
    note: 'Direct grant: alice has alpha-blog-writer (post:read, post:write, comment:read).',
    user: 'alice', client: 'alpha-blog-spa' },

  { name: 'Alice on beta-shop-spa (multi-microservice)',
    note: 'Alice has NO beta-shop grants → all 3 blocks should be empty / absent permissions.',
    user: 'alice', client: 'beta-shop-spa' },

  { name: 'Alice on cross-app-dashboard',
    note: 'Alpha block populated (alice = blog-writer), beta-orders + gamma blocks empty.',
    user: 'alice', client: 'cross-app-dashboard' },

  { name: 'Bob (Realm Admin) on cross-app-dashboard',
    note: 'realm:admin → ALL three blocks pre-expanded to the full catalog of each App.',
    user: 'bob', client: 'cross-app-dashboard' },

  { name: 'Charlie (Beta Shop Admin) on beta-shop-spa',
    note: 'order:admin/product:admin/shipment:admin → each block pre-expands to its respective resource\'s actions.',
    user: 'charlie', client: 'beta-shop-spa' },

  { name: 'Charlie on gamma-crm-mini-ui (no grants there)',
    note: 'charlie has no gamma-crm grants → empty block.',
    user: 'charlie', client: 'gamma-crm-mini-ui' },

  { name: 'Diana (pure auth, no grants) on alpha-blog-spa',
    note: 'WordPress-class user — no roles, no permissions. Block exists (audience matches) but is empty.',
    user: 'diana', client: 'alpha-blog-spa' },

  { name: 'Eve (cross-app user) on cross-app-dashboard',
    note: 'eve has alpha-blog-writer + beta-orders-clerk + gamma-crm-user → all 3 blocks populated with respective grants.',
    user: 'eve', client: 'cross-app-dashboard' },

  { name: 'Morgan (comment-only resource-admin) on alpha-blog-spa',
    note: 'comment:admin → pre-expansion fills comment:read/write/admin. post:* stays empty.',
    user: 'morgan', client: 'alpha-blog-spa' },

  { name: 'Nico (logistics partner) on beta-shop-spa',
    note: 'shipment:read + shipment:track only. orders + products blocks empty; shipping has the two grants.',
    user: 'nico', client: 'beta-shop-spa' },

  { name: 'M2M backend (client_credentials)',
    note: 'No user, no resource_access — UserInfo will reject because client_credentials tokens lack the openid scope.',
    m2m: true, client: 'smoke-m2m-backend' },
]

// ── runner ────────────────────────────────────────────────────────────────

const reportLines = []
function pushln(line = '') { console.log(line); reportLines.push(line) }
function pushblock(label, obj) {
  pushln(label)
  pushln('```json')
  pushln(JSON.stringify(obj, null, 2))
  pushln('```')
  pushln()
}

async function runScenario(s, idx) {
  pushln(`## ${idx}. ${s.name}`)
  pushln(`> ${s.note}`)
  pushln()

  try {
    let accessToken, scope
    if (s.m2m) {
      const r = await driveClientCredentials({
        clientId: s.client,
        clientSecret: CLIENT_SECRETS[s.client],
        scopes: CLIENT_SCOPES[s.client],
      })
      accessToken = r.accessToken; scope = r.scope
      pushln(`**Flow:** client_credentials (M2M).`)
    } else {
      const r = await driveAuthCode({
        userName: s.user,
        clientId: s.client,
        clientSecret: CLIENT_SECRETS[s.client],
        scopes: CLIENT_SCOPES[s.client],
      })
      accessToken = r.accessToken; scope = r.scope
      pushln(`**Flow:** auth-code + PKCE as \`${s.user}\`.`)
    }
    pushln()

    const decoded = decodeJwt(accessToken)
    pushblock('**Access-token claims:**', {
      sub: decoded.sub,
      aud: decoded.aud,
      scope: decoded.scope ?? scope,
      iss: decoded.iss,
      client_id: decoded.client_id,
      exp: decoded.exp,
    })

    const userinfo = await fetchUserInfo(accessToken)
    if (userinfo.ok) {
      pushblock(`**/connect/userinfo (${userinfo.status}):**`, userinfo.body)
    } else {
      pushln(`**/connect/userinfo:** HTTP ${userinfo.status}`)
      pushln('```')
      pushln(typeof userinfo.body === 'string' ? userinfo.body : JSON.stringify(userinfo.body, null, 2))
      pushln('```')
      pushln()
    }

    if (TESTAPPS_RESOURCEAPI && !s.m2m) {
      const me = await fetchTestAppsMe(accessToken)
      if (me) {
        const summary = me.ok ? {
          name: me.body?.name,
          sub: me.body?.sub,
          scopes: me.body?.scopes,
          roles: me.body?.roles,
          permissions: me.body?.permissions,
          groups: me.body?.groups,
        } : me.body
        pushblock(`**TestApps.ResourceApi /me (${me.status}):**`, summary)
      }
    }
  } catch (e) {
    pushln(`**ERROR:** ${e.message}`)
    pushln()
  }

  pushln('---')
  pushln()
}

async function main() {
  pushln(`# Cocoar.Auth Smoke Test — Permission Model End-to-End`)
  pushln()
  pushln(`Generated: ${new Date().toISOString()}`)
  pushln(`IdP: ${BASE_URL}`)
  pushln(`TestApps.ResourceApi: ${TESTAPPS_RESOURCEAPI ?? '— (not set; skip RS-side echo)'}`)
  pushln()
  pushln(`Each scenario drives the OAuth flow, decodes the access token, fetches`)
  pushln(`UserInfo, and (when configured) calls TestApps.ResourceApi /me. The lib's`)
  pushln(`claims-transformation is exercised when ResourceApi is reachable; otherwise`)
  pushln(`the IdP-side emission is what's printed.`)
  pushln()
  pushln('---')
  pushln()

  let idx = 1
  for (const s of SCENARIOS) {
    await runScenario(s, idx++)
  }

  // Persist report
  mkdirSync(dirname(REPORT_PATH), { recursive: true })
  writeFileSync(REPORT_PATH, reportLines.join('\n'), 'utf8')
  console.log()
  console.log(`────────────────────────────────────────────────────`)
  console.log(`Report written to: ${REPORT_PATH}`)
}

main().catch(err => {
  console.error('FAILED:', err.stack ?? err.message)
  process.exit(1)
})
