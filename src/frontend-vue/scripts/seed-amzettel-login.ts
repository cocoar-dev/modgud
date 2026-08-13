/*
 * Seeds the amZettel reference login into a realm.
 *
 * Everything goes through the same API the admin UI uses, so this proves the
 * page is authorable rather than injected: create the brand panel as a
 * composition, materialize it into a login document, save that as a variant,
 * and activate it.
 *
 *   NODE_EXTRA_CA_CERTS=/path/to/caddy-root.crt \
 *     node --experimental-strip-types scripts/seed-amzettel-login.ts \
 *     --base-url https://auth-dev.localhost --password '<admin password>'
 */
import { AMZETTEL_BRAND_PANEL } from '../src/page-builder/compositions/amzettelBrandPanel.ts'
import { createAmzettelLoginDocument } from '../src/page-builder/compositions/amzettelLoginPage.ts'

const args = new Map<string, string>()
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i], process.argv[i + 1])

const baseUrl = (args.get('--base-url') ?? 'https://auth-dev.localhost').replace(/\/$/, '')
const userName = args.get('--username') ?? 'admin'
const password = args.get('--password') ?? process.env.MODGUD_ADMIN_PASSWORD
if (!password) throw new Error('Pass --password or set MODGUD_ADMIN_PASSWORD.')

let cookie = ''

async function api(path: string, init: RequestInit = {}): Promise<any> {
  const response = await fetch(`${baseUrl}${path}`, {
    ...init,
    headers: {
      'content-type': 'application/json',
      ...(cookie ? { cookie } : {}),
      ...(init.headers ?? {}),
    },
  })
  const setCookie = response.headers.getSetCookie?.() ?? []
  if (setCookie.length) cookie = setCookie.map(c => c.split(';')[0]).join('; ')
  const text = await response.text()
  if (!response.ok) throw new Error(`${init.method ?? 'GET'} ${path} → ${response.status}: ${text.slice(0, 300)}`)
  return text ? JSON.parse(text) : null
}

await api('/api/account/login', {
  method: 'POST',
  body: JSON.stringify({ UserName: userName, Password: password, RememberMe: false }),
})
console.log(`angemeldet als ${userName}`)

// The API sets PropertyNamingPolicy = null, so every payload is PascalCase.
// 1. The brand panel becomes a composition the realm owns. Reuse an existing
//    one so re-running does not pile up duplicates.
const existing: any[] = await api('/api/admin/customization/compositions')
let definition = existing.find(c => c.Name === AMZETTEL_BRAND_PANEL.name)

if (definition) {
  // The list returns summaries; fetch the full definition for its root.
  definition = await api(`/api/admin/customization/compositions/${definition.Id}`)

  // Published versions are immutable, so a changed panel is a new version
  // rather than an edit. Pages stay pinned to the version they materialized
  // until their author updates them — which is why the variant below is
  // rewritten against whatever version we end up with.
  const unchanged = JSON.stringify(definition.Root) === JSON.stringify(AMZETTEL_BRAND_PANEL.root)
  if (unchanged) {
    console.log(`Composition unverändert: ${definition.Id} v${definition.Version}`)
  } else {
    definition = await api(`/api/admin/customization/compositions/${definition.Id}/versions`, {
      method: 'POST',
      body: JSON.stringify({ BaseVersion: String(definition.Version), Root: AMZETTEL_BRAND_PANEL.root }),
    })
    console.log(`Composition-Version veröffentlicht: ${definition.Id} v${definition.Version}`)
  }
} else {
  definition = await api('/api/admin/customization/compositions', {
    method: 'POST',
    body: JSON.stringify({ Name: AMZETTEL_BRAND_PANEL.name, Root: AMZETTEL_BRAND_PANEL.root }),
  })
  console.log(`Composition angelegt: ${definition.Id} v${definition.Version}`)
}

// 2. Materialize it into the login document, pinning the version the server
//    actually holds.
const document = createAmzettelLoginDocument({
  id: definition.Id,
  name: definition.Name,
  version: String(definition.Version),
  root: definition.Root ?? AMZETTEL_BRAND_PANEL.root,
})

const variant = await api('/api/admin/customization/pages/login/variants', {
  method: 'POST',
  body: JSON.stringify({ Name: 'amZettel', Schema: JSON.stringify(document) }),
})
console.log(`Variante angelegt: ${variant.Id ?? JSON.stringify(variant).slice(0, 120)}`)

// 3. Activate it for the realm.
await api('/api/admin/customization/pages/login/active', {
  method: 'PUT',
  body: JSON.stringify({ ActiveVariantId: variant.Id }),
})
console.log('als aktive Login-Seite gesetzt')

// 4. Brand colour, radii and fonts are NOT in the document — they belong to
//    the application theme (ADR-0011), and both the sealed panel and the form
//    read them from there. That separation is the point: the same realm-owned
//    variant renders in each application's own colours without editing a node.
//
//    Which also means the theme only applies when an application is in
//    context, i.e. on its own subdomain. So amZettel is modelled as what it
//    actually is — an application of this realm, reached at its own host,
//    inheriting the realm's active login variant.
const settings = {
  Origin: { Subdomain: `amzettel.${args.get('--realm-domain') ?? 'auth-dev.localhost'}` },
  PageTheme: {
    AccentColor: '#10b981',
    ButtonRadiusPx: 999,
    InputRadiusPx: 12,
    CardRadiusPx: 20,
    BodyFontFamily: 'Instrument Sans Variable',
    TitleFontFamily: 'Fraunces Variable',
  },
  Branding: { ProductName: 'amZettel' },
}

const apps: any[] = await api('/api/app')
let app = apps.find(a => (a.Name ?? a.DisplayName) === 'amZettel')

if (app) {
  await api(`/api/app/${app.Id}`, {
    method: 'PUT',
    body: JSON.stringify({
      DisplayName: 'amZettel',
      Description: 'Reference application — the login rebuilt in the PageBuilder.',
      Permissions: app.Permissions ?? [],
      Settings: settings,
    }),
  })
  console.log(`Anwendung aktualisiert: ${app.Id}`)
} else {
  app = await api('/api/app', {
    method: 'POST',
    body: JSON.stringify({
      Slug: 'amzettel',
      DisplayName: 'amZettel',
      Description: 'Reference application — the login rebuilt in the PageBuilder.',
      Permissions: [],
      Settings: settings,
    }),
  })
  console.log(`Anwendung angelegt: ${app.Id}`)
}

console.log(`\nfertig → https://${settings.Origin.Subdomain}/login`)
