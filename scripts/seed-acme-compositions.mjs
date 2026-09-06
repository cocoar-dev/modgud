#!/usr/bin/env node

/**
 * Migrates the local AcmeList auth customization from a monolithic Login
 * document to a reusable PageBuilder composition and consumes the same pinned
 * version from Login and Logout.
 *
 * Usage:
 *   node scripts/seed-acme-compositions.mjs --password <admin-password>
 */

const args = new Map()
for (let index = 2; index < process.argv.length; index += 2) {
  args.set(process.argv[index], process.argv[index + 1])
}

const baseUrl = (args.get('--base-url') ?? 'http://auth-dev.localhost:4310').replace(/\/$/, '')
const userName = args.get('--username') ?? 'codex'
const password = args.get('--password') ?? process.env.MODGUD_ADMIN_PASSWORD

if (!password) {
  throw new Error('Pass --password or set MODGUD_ADMIN_PASSWORD.')
}

const cookies = new Map()

function rememberCookies(response) {
  const values = typeof response.headers.getSetCookie === 'function'
    ? response.headers.getSetCookie()
    : [response.headers.get('set-cookie')].filter(Boolean)
  for (const value of values) {
    const pair = value.split(';', 1)[0]
    const separator = pair.indexOf('=')
    if (separator > 0) cookies.set(pair.slice(0, separator), pair.slice(separator + 1))
  }
}

async function request(path, options = {}) {
  const headers = new Headers(options.headers)
  headers.set('Accept', 'application/json')
  if (options.body !== undefined && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  if (cookies.size) {
    headers.set('Cookie', [...cookies].map(([key, value]) => `${key}=${value}`).join('; '))
  }
  const response = await fetch(`${baseUrl}${path}`, { ...options, headers })
  rememberCookies(response)
  const text = await response.text()
  if (!response.ok) {
    let detail = text
    try { detail = JSON.parse(text)?.Message ?? text } catch {}
    throw new Error(`${options.method ?? 'GET'} ${path} failed (${response.status}): ${detail}`)
  }
  return text ? JSON.parse(text) : null
}

function clone(value) {
  return JSON.parse(JSON.stringify(value))
}

function stripCompositionMetadata(node) {
  const result = clone(node)
  const walk = (current) => {
    delete current.composition
    delete current.compositionOrigins
    for (const child of current.children ?? []) walk(child)
  }
  walk(result)
  return result
}

function findNodeLocation(root, predicate) {
  const visit = (node, parent = null, index = -1) => {
    if (predicate(node)) return { node, parent, index }
    for (let childIndex = 0; childIndex < (node.children ?? []).length; childIndex += 1) {
      const found = visit(node.children[childIndex], node, childIndex)
      if (found) return found
    }
    return null
  }
  return visit(root)
}

function materializeComposition(root, composition, instanceId, instanceName) {
  let childNumber = 0
  const walk = (source, isRoot = false) => {
    const result = clone(source)
    result.id = isRoot
      ? instanceId
      : `${instanceId}-${++childNumber}`
    if (isRoot) result.name = instanceName
    result.compositionOrigins = [{ id: composition.id, sourceNodeId: source.id }]
    if (source.children) result.children = source.children.map(child => walk(child))
    if (isRoot) {
      result.composition = { id: composition.id, version: composition.version }
    }
    return result
  }
  return walk(root, true)
}

function stableJson(value) {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`
  }
  return JSON.stringify(value)
}

function createLogoutPage(compositionRoot, composition) {
  return {
    id: 'acme-logout-page',
    type: 'page',
    schemaVersion: 5,
    enterSubmits: false,
    style: {
      minHeight: '100%',
      width: '100%',
      padding: '0px',
      surface: 'default',
    },
    children: [
      {
        id: 'acme-logout-shell',
        type: 'stack',
        name: 'acmeLogoutShell',
        props: { direction: 'column' },
        style: {
          size: 'fill',
          minHeight: '100%',
        },
        responsive: {
          tablet: { direction: 'row' },
        },
        children: [
          materializeComposition(
            compositionRoot,
            composition,
            'acme-logout-visual-panel',
            'acmeLogoutVisualPanel',
          ),
          {
            id: 'acme-logout-content',
            type: 'stack',
            name: 'acmeLogoutContent',
            props: { direction: 'column' },
            style: {
              size: 'fill',
              minHeight: '100%',
              justify: 'center',
              align: 'center',
              gap: '22px',
              padding: '24px 16px',
              surface: 'subtle',
            },
            responsive: {
              phone: { padding: '32px 24px' },
              tablet: { padding: '48px 32px' },
            },
            children: [
              {
                id: 'acme-logout-card',
                type: 'card',
                name: 'acmeLogoutCard',
                props: {},
                style: {
                  size: 'fill',
                  maxWidth: '420px',
                  surface: 'default',
                  borderTone: 'neutral',
                  borderWidth: '1px',
                  elevation: 'small',
                  gap: '18px',
                  padding: '34px 36px 26px',
                  align: 'center',
                  textAlign: 'center',
                },
                children: [
                  {
                    id: 'acme-logout-heading',
                    type: 'heading',
                    name: 'acmeLogoutHeading',
                    props: {
                      text: {
                        source: 'translation',
                        key: 'page.logoutHeading',
                        fallback: 'Signed out',
                      },
                      level: 2,
                    },
                    style: {
                      alignSelf: 'center',
                      fontFamily: 'heading',
                      fontSize: 'xlarge',
                      fontWeight: 'bold',
                      lineHeight: 'tight',
                    },
                  },
                  {
                    id: 'acme-logout-copy',
                    type: 'paragraph',
                    name: 'acmeLogoutCopy',
                    props: {
                      text: {
                        source: 'translation',
                        key: 'page.logoutCopy',
                        fallback: 'Your AcmeList session has ended safely.',
                      },
                    },
                    style: {
                      alignSelf: 'center',
                      foreground: 'secondary',
                      lineHeight: 'normal',
                    },
                  },
                  {
                    id: 'acme-logout-action-error',
                    type: 'feedback',
                    name: 'acmeLogoutActionError',
                    props: { kind: 'form-error' },
                  },
                  {
                    id: 'acme-back-to-login',
                    type: 'button',
                    name: 'acmeBackToLogin',
                    props: {
                      label: {
                        source: 'translation',
                        key: 'page.backToLogin',
                        fallback: 'Sign in again',
                      },
                      action: 'auth:back-to-login',
                      variant: 'primary',
                    },
                    style: { size: 'fill' },
                  },
                ],
              },
              {
                id: 'acme-logout-legal',
                type: 'stack',
                name: 'acmeLogoutLegal',
                props: { direction: 'row' },
                style: {
                  justify: 'center',
                  align: 'center',
                  gap: '10px',
                  foreground: 'tertiary',
                  fontSize: 'caption',
                },
                children: [
                  {
                    id: 'acme-logout-privacy',
                    type: 'link',
                    name: 'acmeLogoutPrivacy',
                    props: {
                      label: { source: 'translation', key: 'page.privacy', fallback: 'Privacy' },
                      action: 'legal:privacy',
                    },
                  },
                  {
                    id: 'acme-logout-legal-separator',
                    type: 'paragraph',
                    name: 'acmeLogoutLegalSeparator',
                    props: { text: '·' },
                    style: { foreground: 'tertiary', size: 'fit' },
                  },
                  {
                    id: 'acme-logout-terms',
                    type: 'link',
                    name: 'acmeLogoutTerms',
                    props: {
                      label: { source: 'translation', key: 'page.legalNotice', fallback: 'Legal notice' },
                      action: 'legal:terms',
                    },
                  },
                ],
              },
            ],
          },
        ],
      },
    ],
    translations: {
      de: {
        'page.logoutHeading': 'Abgemeldet',
        'page.logoutCopy': 'Deine AcmeList-Sitzung wurde sicher beendet.',
        'page.backToLogin': 'Erneut anmelden',
        'page.privacy': 'Datenschutz',
        'page.legalNotice': 'Impressum',
      },
      en: {
        'page.logoutHeading': 'Signed out',
        'page.logoutCopy': 'Your AcmeList session has ended safely.',
        'page.backToLogin': 'Sign in again',
        'page.privacy': 'Privacy',
        'page.legalNotice': 'Legal notice',
      },
    },
    rootCode: `definePageRoot({
  compute(page) {
    page.style.minHeight = '100%'
    page.style.width = '100%'
    page.style.padding = '0px'
  },
})`,
  }
}

async function saveAndPublishVariant(slot, summary, schema) {
  const full = await request(`/api/admin/customization/pages/${encodeURIComponent(slot)}/variants/${encodeURIComponent(summary.Id)}`)
  if (stableJson(JSON.parse(full.Schema)) === stableJson(schema) && full.IsPublished && !full.HasUnpublishedChanges) {
    return summary.Id
  }
  await request(
    `/api/admin/customization/pages/${encodeURIComponent(slot)}/variants/${encodeURIComponent(summary.Id)}`,
    {
      method: 'PUT',
      body: JSON.stringify({ Name: summary.Name, Schema: JSON.stringify(schema) }),
    },
  )
  await request(
    `/api/admin/customization/pages/${encodeURIComponent(slot)}/variants/${encodeURIComponent(summary.Id)}/publish`,
    { method: 'POST' },
  )
  return summary.Id
}

await request('/api/account/login', {
  method: 'POST',
  body: JSON.stringify({ UserName: userName, Password: password, RememberMe: false, ReturnUrl: null }),
})

const [pageLibrary, compositionSummaries, applications] = await Promise.all([
  request('/api/admin/customization/pages'),
  request('/api/admin/customization/compositions'),
  request('/api/app'),
])

const loginSlot = pageLibrary.Slots.find(slot => slot.Slug === 'login')
const loginSummary = loginSlot?.Variants.find(variant =>
  variant.Name === 'AcmeList · Login' || variant.UsedByApps?.includes('AcmeList'))
if (!loginSummary) throw new Error('The AcmeList Login variant was not found.')

const loginVariant = await request(`/api/admin/customization/pages/login/variants/${encodeURIComponent(loginSummary.Id)}`)
const loginSchema = JSON.parse(loginVariant.Schema)
const existingCompositionSummary = compositionSummaries.find(item => item.Name === 'AcmeList · Visual panel')
let compositionDefinition

if (existingCompositionSummary) {
  compositionDefinition = await request(`/api/admin/customization/compositions/${encodeURIComponent(existingCompositionSummary.Id)}`)
} else {
  const visualLocation = findNodeLocation(loginSchema, node =>
    node.id === 'acme-brand-panel'
    || node.id === 'acme-visual-panel'
    || (node.type === 'visual-markup' && (node.props?.html ?? node.props?.markup ?? '').includes('auth-brand')))
  if (!visualLocation) throw new Error('The AcmeList visual panel was not found in the Login variant.')
  compositionDefinition = await request('/api/admin/customization/compositions', {
    method: 'POST',
    body: JSON.stringify({
      Name: 'AcmeList · Visual panel',
      Root: stripCompositionMetadata(visualLocation.node),
    }),
  })
}

const composition = {
  id: compositionDefinition.Id,
  version: compositionDefinition.Version,
}
const compositionRoot = stripCompositionMetadata(compositionDefinition.Root)

const currentVisualLocation = findNodeLocation(loginSchema, node =>
  node.composition?.id === composition.id
  || node.id === 'acme-brand-panel'
  || node.id === 'acme-visual-panel'
  || (node.type === 'visual-markup' && (node.props?.html ?? node.props?.markup ?? '').includes('auth-brand')))
if (!currentVisualLocation?.parent) throw new Error('The AcmeList visual panel has no replaceable parent.')
currentVisualLocation.parent.children[currentVisualLocation.index] = materializeComposition(
  compositionRoot,
  composition,
  'acme-login-visual-panel',
  'acmeLoginVisualPanel',
)
await saveAndPublishVariant('login', loginSummary, loginSchema)

const logoutSchema = createLogoutPage(compositionRoot, composition)
const logoutSlot = pageLibrary.Slots.find(slot => slot.Slug === 'logout')
let logoutSummary = logoutSlot?.Variants.find(variant => variant.Name === 'AcmeList · Logout')
if (!logoutSummary) {
  logoutSummary = await request('/api/admin/customization/pages/logout/variants', {
    method: 'POST',
    body: JSON.stringify({ Name: 'AcmeList · Logout', Schema: JSON.stringify(logoutSchema) }),
  })
}
const logoutVariantId = await saveAndPublishVariant('logout', logoutSummary, logoutSchema)

const acmeList = applications.find(application => application.Slug === 'acmelist')
if (!acmeList) throw new Error('The AcmeList application was not found.')
await request(`/api/app/${encodeURIComponent(acmeList.Id)}/pages/logout/active`, {
  method: 'PUT',
  body: JSON.stringify({ Inherit: false, ActiveVariantId: logoutVariantId }),
})

console.log(JSON.stringify({
  composition: `${compositionDefinition.Name}@${composition.version}`,
  loginVariant: loginSummary.Name,
  logoutVariant: 'AcmeList · Logout',
  application: acmeList.DisplayName,
}, null, 2))
