#!/usr/bin/env node
/**
 * Demo-data seeder — drives the regular admin API as an authenticated client
 * (no backend bypass, no DI registration, no PROD-01 attack surface).
 *
 * Usage:
 *   node scripts/seed-demo.mjs \
 *       [--base-url=http://localhost:9099] \
 *       [--user=admin] [--password=ABC12abc!] \
 *       [--realm=system] \
 *       [--json=src/dotnet/Cocoar.Auth.Api/data/demo-seed.json]
 *
 * Defaults pick up env vars: BASE_URL, SEED_USER, SEED_PASSWORD, SEED_REALM,
 * SEED_JSON. The script is idempotent — entities that already exist by their
 * natural key (UserName, role Name, group Name, scope Name, client_id, API
 * Name, login-provider DisplayName) are skipped.
 *
 * The script prints generated client/API secrets at the end. Capture them from
 * stdout — they are not retrievable later (only re-rotation is, via the API).
 *
 * Tenant scoping: pass --realm=<slug> (or SEED_REALM) to seed a tenant other
 * than `system`. The slug is sent as the HTTP Host header so RealmMiddleware
 * resolves to the right tenant. The realm must exist (created via
 * POST /api/admin/realms by a CP-admin) and the script's logged-in user must
 * be an admin in that realm.
 */

import { readFile } from 'node:fs/promises'
import { resolve as resolvePath, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

// ── argv / env ────────────────────────────────────────────────────────────

const argv = Object.fromEntries(
  process.argv.slice(2)
    .map(a => a.startsWith('--') ? a.slice(2).split('=', 2) : [null, a])
    .filter(([k]) => k !== null)
    .map(([k, v]) => [k, v ?? true]),
)

const HERE = dirname(fileURLToPath(import.meta.url))
const REPO_ROOT = resolvePath(HERE, '..')

const BASE_URL  = argv['base-url'] ?? process.env.BASE_URL ?? 'http://localhost:9099'
const USERNAME  = argv['user']     ?? process.env.SEED_USER ?? 'admin'
const PASSWORD  = argv['password'] ?? process.env.SEED_PASSWORD ?? 'ABC12abc!'
const REALM     = argv['realm']    ?? process.env.SEED_REALM ?? null   // null = use BASE_URL host as-is
const JSON_PATH = argv['json']     ?? process.env.SEED_JSON
                  ?? resolvePath(REPO_ROOT, 'src/dotnet/Cocoar.Auth.Api/data/demo-seed.json')

// ── tiny HTTP client ──────────────────────────────────────────────────────

let cookies = ''

// Compute the effective URL — when --realm is set we swap the hostname in
// BASE_URL so fetch sends Host: <slug>.localhost automatically. (Node's
// native fetch refuses to honour an explicit Host header — it's on the
// forbidden-headers list — so URL rewriting is the only reliable way.)
const TARGET_URL = (() => {
  if (!REALM) return BASE_URL
  const u = new URL(BASE_URL)
  // For dev the convention is "<slug>.localhost" — the realm's default
  // first domain. Override by setting --base-url to a realm-specific URL
  // directly if your domain layout is different.
  u.hostname = `${REALM}.${u.hostname}`
  return u.toString().replace(/\/$/, '')
})()

async function request(method, path, body = null) {
  const headers = {
    'Accept': 'application/json',
    'Cookie': cookies,
  }
  if (body !== null) headers['Content-Type'] = 'application/json'

  const res = await fetch(`${TARGET_URL}${path}`, {
    method,
    headers,
    body: body !== null ? JSON.stringify(body) : undefined,
    redirect: 'manual',
  })

  // Capture Set-Cookie — Node fetch returns them via getSetCookie() (Node 19.7+)
  // or as a comma-joined header (older). We need the raw values so the auth
  // cookie survives login.
  const setCookies = typeof res.headers.getSetCookie === 'function'
    ? res.headers.getSetCookie()
    : (res.headers.get('set-cookie')?.split(/,(?=[^;]+=)/) ?? [])
  for (const sc of setCookies) {
    const eq = sc.indexOf('=')
    const semi = sc.indexOf(';')
    if (eq < 0) continue
    const name = sc.slice(0, eq)
    const value = sc.slice(eq + 1, semi < 0 ? undefined : semi)
    cookies = cookies
      .split('; ')
      .filter(c => c && !c.startsWith(name + '='))
      .concat([`${name}=${value}`])
      .join('; ')
  }

  let text = await res.text()
  let parsed = null
  if (text) {
    try { parsed = JSON.parse(text) } catch { parsed = text }
  }
  return { ok: res.ok, status: res.status, body: parsed }
}

const get  = (p)       => request('GET',  p)
const post = (p, body) => request('POST', p, body)
const put  = (p, body) => request('PUT',  p, body)

// ── auth ──────────────────────────────────────────────────────────────────

async function login() {
  const res = await post('/api/account/login', { UserName: USERNAME, Password: PASSWORD })
  if (!res.ok) throw new Error(`Login failed: HTTP ${res.status} — ${JSON.stringify(res.body)}`)
  console.log(`✓ Logged in as ${USERNAME}`)
}

// ── helpers ───────────────────────────────────────────────────────────────

function fail(msg, res) {
  const detail = typeof res.body === 'string' ? res.body : JSON.stringify(res.body)
  console.warn(`  ! ${msg}: HTTP ${res.status} — ${detail}`)
}

// ── seeding phases ────────────────────────────────────────────────────────

const idsByKey = {
  users: new Map(),  // demo-seed key  → ShortGuid string
  roles: new Map(),  // demo-seed key OR @PascalKey → ShortGuid
}

async function seedRoles(spec) {
  // Index existing roles so we can resolve "@SystemAdmin" / "@UserManager" /
  // "@Viewer" references and skip duplicates by Name.
  const existing = await get('/api/role')
  if (!existing.ok) throw new Error('GET /api/role failed: ' + existing.status)
  for (const r of existing.body) {
    idsByKey.roles.set(r.Name, r.Id)
    idsByKey.roles.set('@' + r.Name.replace(/ /g, ''), r.Id)
  }

  let created = 0
  for (const r of spec) {
    if (idsByKey.roles.has(r.name)) {
      console.log(`  — Role '${r.name}' exists — skipping`)
      continue
    }
    const res = await post('/api/role', {
      Name: r.name,
      Description: r.description ?? null,
      ResourceType: r.resource && r.resource.length > 0 ? r.resource : 'app',
      Permissions: r.permissions ?? [],
      AppSlug: 'cocoar-auth',
    })
    if (!res.ok) { fail(`role '${r.name}'`, res); continue }
    const id = res.body.Id ?? res.body.id
    idsByKey.roles.set(r.name, id)
    idsByKey.roles.set(r.key, id)
    created++
    console.log(`  ✓ Role '${r.name}'`)
  }
  return created
}

async function seedUsers(spec, password) {
  // Index existing by UserName.
  const existing = await get('/api/user')
  if (!existing.ok) throw new Error('GET /api/user failed: ' + existing.status)
  // The user-list endpoint returns { Items: [...] } in some shapes; tolerate both.
  const items = Array.isArray(existing.body) ? existing.body
              : Array.isArray(existing.body?.Items) ? existing.body.Items
              : []
  for (const u of items) {
    if (u.UserName) idsByKey.users.set(u.UserName, u.Id)
  }

  let created = 0
  for (const u of spec) {
    const userName = (u.userName ?? u.key ?? '').toLowerCase()
    if (idsByKey.users.has(userName)) {
      console.log(`  — User '${userName}' exists — skipping`)
      idsByKey.users.set(u.key, idsByKey.users.get(userName))
      continue
    }
    const res = await post('/api/user', {
      UserName:  userName,
      Firstname: u.firstname,
      Lastname:  u.lastname,
      Acronym:   u.acronym,
      Email:     u.email,
      Password:  password,
    })
    if (!res.ok) { fail(`user '${userName}'`, res); continue }
    const id = res.body.Id ?? res.body.id
    idsByKey.users.set(u.key, id)
    idsByKey.users.set(userName, id)
    created++
    console.log(`  ✓ User '${userName}'`)
  }
  return created
}

function resolveMembers(keys) {
  const out = []
  for (const k of keys ?? []) {
    const id = idsByKey.users.get(k)
    if (id) out.push(id)
    else console.warn(`  ! Unknown member key '${k}' — skipping`)
  }
  return out
}

function resolveRoles(keys) {
  const out = []
  for (const k of keys ?? []) {
    const id = idsByKey.roles.get(k)
    if (id) out.push(id)
    else console.warn(`  ! Unknown role key '${k}' — skipping`)
  }
  return out
}

async function seedGroups(spec) {
  const existing = await get('/api/group')
  if (!existing.ok) throw new Error('GET /api/group failed: ' + existing.status)
  const existingNames = new Set(
    (Array.isArray(existing.body) ? existing.body : (existing.body?.Items ?? []))
      .map(g => g.Name?.toLowerCase()))

  let created = 0
  for (const g of spec) {
    if (existingNames.has(g.name?.toLowerCase())) {
      console.log(`  — Group '${g.name}' exists — skipping`)
      continue
    }
    const isAuto = (g.membershipMode ?? '').toLowerCase() === 'auto'
    const res = await post('/api/group', {
      Name: g.name,
      Description: g.description ?? null,
      MemberIds: resolveMembers(g.members),
      RoleIds:   resolveRoles(g.roles),
      MembershipMode: isAuto ? 'Auto' : 'Manual',
      MembershipScript: isAuto ? g.membershipScript : null,
      BoundTo: ['cocoar-auth'],
    })
    if (!res.ok) { fail(`group '${g.name}'`, res); continue }
    created++
    console.log(`  ✓ Group '${g.name}'`)
  }
  return created
}

async function seedScopes(spec) {
  const existing = await get('/api/admin/oauth/scopes')
  if (!existing.ok) throw new Error('GET scopes failed: ' + existing.status)
  const existingNames = new Set(
    (existing.body?.Items ?? []).map(s => s.Name?.toLowerCase()))

  let created = 0
  for (const s of spec) {
    if (existingNames.has(s.name.toLowerCase())) {
      console.log(`  — Scope '${s.name}' exists — skipping`)
      continue
    }
    const res = await post('/api/admin/oauth/scopes', {
      Name: s.name,
      DisplayName: s.displayName,
      Description: s.description,
      Resources: s.resources?.length ? s.resources : ['demo-api'],
      Enabled: true,
      ShowInDiscoveryDocument: true,
    })
    if (!res.ok) { fail(`scope '${s.name}'`, res); continue }
    created++
    console.log(`  ✓ Scope '${s.name}'`)
  }
  return created
}

async function seedApis(spec) {
  const existing = await get('/api/admin/oauth/apis')
  if (!existing.ok) throw new Error('GET apis failed: ' + existing.status)
  const existingNames = new Set(
    (existing.body?.Items ?? []).map(a => a.Name?.toLowerCase()))

  const secrets = {}
  let created = 0
  for (const a of spec) {
    if (existingNames.has(a.name.toLowerCase())) {
      console.log(`  — API '${a.name}' exists — skipping`)
      continue
    }
    const res = await post('/api/admin/oauth/apis', {
      Name: a.name,
      DisplayName: a.displayName,
      Description: a.description,
      Enabled: true,
      Scopes: a.scopes ?? [],
      UserClaims: a.userClaims ?? [],
    })
    if (!res.ok) { fail(`api '${a.name}'`, res); continue }
    if (res.body.ApiSecret) secrets[a.name] = res.body.ApiSecret
    created++
    console.log(`  ✓ API '${a.name}'`)
  }
  return { created, secrets }
}

async function seedClients(spec) {
  const existing = await get('/api/admin/oauth/clients')
  if (!existing.ok) throw new Error('GET clients failed: ' + existing.status)
  const existingClientIds = new Set(
    (existing.body?.Items ?? []).map(c => c.ClientId?.toLowerCase()))

  const secrets = {}
  let created = 0
  for (const c of spec) {
    if (existingClientIds.has(c.clientId.toLowerCase())) {
      console.log(`  — Client '${c.clientId}' exists — skipping`)
      continue
    }
    const res = await post('/api/admin/oauth/clients', {
      ClientId: c.clientId,
      DisplayName: c.displayName,
      ClientType: c.clientType,
      ClientSecret: c.clientSecret,
      ConsentType: c.consentType ?? 'implicit',
      RedirectUris: c.redirectUris ?? [],
      PostLogoutRedirectUris: c.postLogoutRedirectUris ?? [],
      Scopes: c.scopes ?? [],
      AllowedGrantTypes: c.allowedGrantTypes ?? [],
      RequireConsent: c.requireConsent ?? false,
      RequireClientSecret: c.requireClientSecret ?? (c.clientType === 'confidential'),
      AccessTokenType: c.accessTokenType ?? 'Reference',
      Enabled: true,
    })
    if (!res.ok) { fail(`client '${c.clientId}'`, res); continue }
    if (res.body.ClientSecret) secrets[c.clientId] = res.body.ClientSecret
    created++
    console.log(`  ✓ Client '${c.clientId}'`)
  }
  return { created, secrets }
}

async function seedLoginProviders(spec) {
  const existing = await get('/api/admin/login-providers')
  if (!existing.ok) throw new Error('GET login-providers failed: ' + existing.status)
  const existingNames = new Set(
    (Array.isArray(existing.body) ? existing.body : (existing.body?.Items ?? []))
      .map(p => p.DisplayName?.toLowerCase()))

  let created = 0
  for (const p of spec) {
    if (existingNames.has(p.displayName.toLowerCase())) {
      console.log(`  — Login provider '${p.displayName}' exists — skipping`)
      continue
    }
    const res = await post('/api/admin/login-providers', {
      Flavor: p.flavor ?? '',
      DisplayName: p.displayName,
      FlavorData: p.flavorData ?? null,
      Type: p.type ?? 'Oidc',
      Description: p.description ?? null,
    })
    if (!res.ok) { fail(`login-provider '${p.displayName}'`, res); continue }
    created++
    console.log(`  ✓ Login provider '${p.displayName}'`)
  }
  return created
}

// ── main ──────────────────────────────────────────────────────────────────

async function main() {
  console.log(`Seeding from ${JSON_PATH}`)
  console.log(`Target: ${TARGET_URL}${REALM ? ` (realm '${REALM}')` : ''}`)
  console.log()

  const data = JSON.parse(await readFile(JSON_PATH, 'utf8'))

  await login()

  console.log('\n[1/7] Roles')
  const rolesCreated = await seedRoles(data.roles ?? [])

  console.log('\n[2/7] Users')
  const usersCreated = await seedUsers(data.users ?? [], data.password)

  console.log('\n[3/7] Groups')
  const groupsCreated = await seedGroups(data.groups ?? [])

  console.log('\n[4/7] OAuth Scopes')
  const scopesCreated = await seedScopes(data.scopes ?? [])

  console.log('\n[5/7] OAuth APIs')
  const { created: apisCreated, secrets: apiSecrets } = await seedApis(data.apis ?? [])

  console.log('\n[6/7] OAuth Clients')
  const { created: clientsCreated, secrets: clientSecrets } = await seedClients(data.clients ?? [])

  console.log('\n[7/7] Login Providers')
  const providersCreated = await seedLoginProviders(data.loginProviders ?? [])

  console.log('\n──────────────────────────────────────────')
  console.log('Done.')
  console.log(`  Roles:           ${rolesCreated}`)
  console.log(`  Users:           ${usersCreated}  (password: ${data.password})`)
  console.log(`  Groups:          ${groupsCreated}`)
  console.log(`  Scopes:          ${scopesCreated}`)
  console.log(`  APIs:            ${apisCreated}`)
  console.log(`  Clients:         ${clientsCreated}`)
  console.log(`  Login providers: ${providersCreated}`)

  if (Object.keys(apiSecrets).length || Object.keys(clientSecrets).length) {
    console.log('\nGenerated secrets — capture these now, they are not retrievable later:')
    for (const [name, sec] of Object.entries(apiSecrets))
      console.log(`  api    ${name}: ${sec}`)
    for (const [id, sec] of Object.entries(clientSecrets))
      console.log(`  client ${id}: ${sec}`)
  }
}

main().catch(err => {
  console.error('FAILED:', err.message)
  process.exit(1)
})
