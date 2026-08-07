import {
  createAuthPageConfig as createPresetConfig,
  createAuthPageDocument,
  definePageElement,
  setElementQuickProperty,
  type AuthPageLocale,
  type AuthPageSlot,
  type ElementNode,
  type EmptyProps,
  type NodeStyle,
  type PageConfig,
  type PageNode,
  type PagePreviewFixture,
  type PageRootNode,
  type PageStylePreset,
} from '@cocoar/vue-page-builder'
import BrandHeaderElement from '@/components/page-builder/BrandHeaderElement.vue'
import BrandHeaderPreview from '@/components/page-builder/BrandHeaderPreview.vue'
import type { PageThemeConfig } from '@/stores/appconfig.store'
import { createAuthVisualMarkupConfig } from './authVisualMarkup'

export type { AuthPageLocale, AuthPageSlot }

export const AUTH_PAGE_SLOTS: AuthPageSlot[] = [
  'login',
  'password-forgot',
  'logout',
  'consent',
]

const modgudElements = {
  'modgud-brand-header': definePageElement<EmptyProps>({
    renderer: BrandHeaderElement,
    builder: {
      label: { key: 'modgud.pageBuilder.brandHeader', fallback: 'Application branding' },
      icon: 'image',
      defaults: () => ({}),
      preview: BrandHeaderPreview,
    },
  }),
}

// Host-owned, deployment-time CSS affordances. Documents persist only the
// allowlisted id; both Builder preview and runtime receive this same config.
const authPageStylePresets: PageStylePreset[] = [
  {
    id: 'amzettel-auth-visual-panel',
    label: 'amZettel · Auth visual panel',
    className: 'auth-preset-amzettel-visual-panel',
    allowedOn: ['page', 'stack'],
  },
]

function findNode(root: PageNode, id: string): PageNode | undefined {
  if (root.id === id) return root
  if (!('children' in root) || !Array.isArray(root.children)) return undefined
  for (const child of root.children) {
    const found = findNode(child, id)
    if (found) return found
  }
}

function patchElementStyle(node: PageNode | undefined, patch: Partial<NodeStyle>): void {
  if (!node || node.type === 'page') return
  const element = node as ElementNode
  element.style = { ...(element.style ?? {}) }
  for (const [key, value] of Object.entries(patch)) {
    if (value === undefined) delete (element.style as Record<string, unknown>)[key]
    else (element.style as Record<string, unknown>)[key] = value
    element.elementCode = setElementQuickProperty(element.elementCode, `style.${key}`, value)
  }
}

function patchElementProps(node: ElementNode, patch: Record<string, unknown>): void {
  node.props = { ...(node.props ?? {}), ...patch }
  for (const [key, value] of Object.entries(patch)) {
    node.elementCode = setElementQuickProperty(node.elementCode, `props.${key}`, value)
  }
}

/** Align the generic auth preset with Modgud's fixed LoginView contract. */
function alignLoginTemplate(schema: PageRootNode): void {
  schema.style = {
    ...(schema.style ?? {}),
    minHeight: '100%',
    padding: '16px',
    surface: 'default',
    justify: 'space-between',
  }
  schema.responsive = undefined

  const languageSwitcher: ElementNode = {
    id: 'login-language-switcher',
    type: 'link',
    name: 'loginLanguageSwitcher',
    props: {},
  }
  patchElementProps(languageSwitcher, {
    label: {
      source: 'translation',
      key: 'page.languageSwitcher.label',
      fallback: 'EN',
    },
    action: 'auth:toggle-language',
  })
  patchElementStyle(languageSwitcher, {
    foreground: 'tertiary',
    fontSize: 'caption',
    lineHeight: 'tight',
    size: 'fit',
  })

  const languageZone: ElementNode = {
    id: 'login-language-zone',
    type: 'stack',
    name: 'loginLanguageZone',
    props: { direction: 'row' },
    children: [languageSwitcher],
  }
  patchElementStyle(languageZone, {
    align: 'center',
    height: '14.4px',
    justify: 'end',
    size: 'fill',
  })

  // Balance the switcher at the bottom so the auth frame remains exactly
  // centered without relying on absolute positioning or overlay behavior.
  const languageBalance: ElementNode = {
    id: 'login-language-balance',
    type: 'stack',
    name: 'loginLanguageBalance',
    props: { direction: 'column' },
    children: [],
  }
  patchElementStyle(languageBalance, {
    height: '14.4px',
    size: 'fixed',
    width: '1px',
  })

  schema.children = [
    languageZone,
    ...(schema.children ?? []),
    languageBalance,
  ]
  schema.translations = {
    ...(schema.translations ?? {}),
    de: {
      ...(schema.translations?.de ?? {}),
      'page.languageSwitcher.label': 'EN',
    },
    en: {
      ...(schema.translations?.en ?? {}),
      'page.languageSwitcher.label': 'DE',
    },
  }

  patchElementStyle(findNode(schema, 'auth-frame'), { gap: '32px' })
  patchElementStyle(findNode(schema, 'login-subtitle'), {
    foreground: 'primary',
    fontSize: 'small',
    lineHeight: 'normal',
  })
  patchElementStyle(findNode(schema, 'login-card'), { elevation: 'small', radius: undefined })
  patchElementStyle(findNode(schema, 'alternative-divider'), { gap: '12px' })
  patchElementStyle(findNode(schema, 'divider-label'), {
    foreground: 'primary',
    fontSize: 'caption',
    lineHeight: 'tight',
  })
  patchElementStyle(findNode(schema, 'providers'), { gap: '16px' })
  patchElementStyle(findNode(schema, 'forgot-link'), {
    foreground: 'primary',
    fontSize: 'small',
    lineHeight: 'normal',
    size: 'fill',
  })
  patchElementStyle(findNode(schema, 'register-link'), {
    foreground: 'primary',
    fontSize: 'small',
    lineHeight: 'normal',
    size: 'fill',
  })

  const dividerLabel = findNode(schema, 'divider-label') as ElementNode | undefined
  if (dividerLabel) {
    dividerLabel.visibleWhen = {
      source: 'context',
      path: 'auth.internalLoginEnabled',
      operator: 'equals',
      value: true,
    }
  }

  // An empty repeat must not consume a flex-gap slot. This is particularly
  // visible in realms without external identity providers.
  const providers = findNode(schema, 'providers') as ElementNode | undefined
  if (providers) {
    providers.visibleWhen = {
      source: 'context',
      path: 'auth.externalProviders',
      operator: 'isNotEmpty',
    }
  }

  const credentials = findNode(schema, 'credentials')
  if (credentials && 'children' in credentials && Array.isArray(credentials.children)) {
    const actionErrorIndex = credentials.children.findIndex(child => child.id === 'login-action-error')
    if (actionErrorIndex >= 0) {
      credentials.children.splice(actionErrorIndex, 0, {
        id: 'login-context-error',
        type: 'note',
        name: 'loginContextError',
        props: { variant: 'error', text: '' },
        bindings: {
          text: { source: 'context', path: 'feedback.message' },
        },
        visibleWhen: {
          source: 'context',
          path: 'feedback.message',
          operator: 'isNotEmpty',
        },
      })
    }
  }

  const card = findNode(schema, 'login-card')
  if (card && 'children' in card && Array.isArray(card.children)) {
    const passwordlessIndex = card.children.findIndex(child => child.id === 'passwordless-info')
    if (passwordlessIndex >= 0) {
      card.children.splice(passwordlessIndex + 1, 0,
        {
          id: 'login-alternative-context-error',
          type: 'note',
          name: 'loginAlternativeContextError',
          props: { variant: 'error', text: '' },
          bindings: {
            text: { source: 'context', path: 'feedback.message' },
          },
          visibleWhen: {
            all: [
              { source: 'context', path: 'feedback.message', operator: 'isNotEmpty' },
              {
                any: [
                  { source: 'context', path: 'auth.passwordless', operator: 'equals', value: true },
                  { source: 'context', path: 'auth.internalLoginEnabled', operator: 'equals', value: false },
                ],
              },
            ],
          },
        },
        {
          id: 'login-alternative-action-error',
          type: 'feedback',
          name: 'loginAlternativeActionError',
          props: { kind: 'form-error' },
          visibleWhen: {
            any: [
              { source: 'context', path: 'auth.passwordless', operator: 'equals', value: true },
              { source: 'context', path: 'auth.internalLoginEnabled', operator: 'equals', value: false },
            ],
          },
        },
      )
    }
  }
}

export function authPageLocale(language: string): AuthPageLocale {
  return language.toLowerCase().startsWith('de') ? 'de' : 'en'
}

export function createAuthPageConfig(
  slot: AuthPageSlot,
  locale: AuthPageLocale = 'en',
  pickAsset?: (currentId?: string) => Promise<string | null>,
  pageTheme?: PageThemeConfig | null,
): PageConfig {
  const preset = createPresetConfig(slot, locale)
  const previewFixtures = (preset.previewFixtures ?? []).flatMap<PagePreviewFixture>((fixture) => [
    {
      ...fixture,
      id: `${fixture.id}-desktop`,
      label: `${fixture.label} · Desktop`,
      viewport: 'desktop',
    },
    {
      ...fixture,
      id: `${fixture.id}-mobile`,
      label: `${fixture.label} · Mobile`,
      viewport: { width: 390, height: 844 },
    },
  ])

  return {
    ...preset,
    stylePresets: [
      ...(preset.stylePresets ?? []),
      ...authPageStylePresets,
    ],
    // Fixtures are a host-owned acceptance contract. Exercise every auth data
    // shape at both a desktop breakpoint and Modgud's narrow mobile viewport.
    previewFixtures,
    availableActions: [
      ...(preset.availableActions ?? []),
      ...(slot === 'login'
        ? [
            {
              id: 'auth:toggle-language',
              label: locale === 'de' ? 'Sprache wechseln' : 'Switch language',
            },
            {
              id: 'auth:request-login-code',
              label: locale === 'de' ? 'Anmeldecode senden' : 'Send login code',
            },
            {
              id: 'auth:verify-login-code',
              label: locale === 'de' ? 'Anmeldecode prüfen' : 'Verify login code',
            },
            {
              id: 'auth:resend-login-code',
              label: locale === 'de' ? 'Anmeldecode erneut senden' : 'Resend login code',
            },
            {
              id: 'auth:back-to-email',
              label: locale === 'de' ? 'Zurück zur E-Mail' : 'Back to email',
            },
          ]
        : []),
    ],
    fields: slot === 'login'
      ? [
          ...(preset.fields ?? []),
          {
            name: 'email',
            valueType: 'string',
            label: locale === 'de' ? 'E-Mail' : 'Email',
            defaultElement: 'text-input',
          },
          {
            name: 'otpCode',
            valueType: 'string',
            label: locale === 'de' ? '6-stelliger Code' : '6-digit code',
            defaultElement: 'otp-input',
          },
        ]
      : preset.fields,
    contextFields: slot === 'login'
      ? [
          ...(preset.contextFields ?? []),
          { path: 'auth.loginEmail', type: 'string' },
        ]
      : preset.contextFields,
    availableStates: slot === 'login'
      ? [
          ...(preset.availableStates ?? []),
          { id: 'login-code', label: locale === 'de' ? 'Anmeldecode' : 'Login code' },
        ]
      : preset.availableStates,
    allowedElements: [
      ...(preset.allowedElements ?? []),
      ...(slot === 'login' ? ['otp-input' as const] : []),
      'modgud-brand-header',
    ],
    elements: { ...(preset.elements ?? {}), ...modgudElements },
    assetResolver: (id: string) => `/api/assets/${encodeURIComponent(id)}`,
    pickAsset,
    visualMarkup: createAuthVisualMarkupConfig(pageTheme ?? null),
  }
}

/**
 * The upstream schema-v4 preset is the source of truth for responsive rules,
 * translations, repeaters, states and Page Code. Modgud replaces only the
 * generic letter mark/product heading with its runtime branding element so
 * the existing logo URL contract remains intact.
 */
export function createDefaultAuthPageSchema(slot: AuthPageSlot): PageNode {
  const schema = createAuthPageDocument(slot)
  const slotName = slot.replace(/-([a-z])/g, (_match, letter: string) => letter.toUpperCase())

  // The auth host and editor preview provide a definite viewport-sized
  // container. Let the document fill that container so 100dvh stays a host
  // concern while Root Page Code can still change the shell and layout.
  if (schema.type === 'page') {
    const root = schema as PageRootNode
    if (slot === 'login') alignLoginTemplate(root)
    root.rootCode = `definePageRoot({
  compute(page) {
    page.style.minHeight = '100%'
    page.style.width = '100%'
    page.style.padding = '16px'
    ${slot === 'login' ? "page.style.surface = 'default'" : ''}
  },
})`
  }

  const walk = (node: PageNode) => {
    if (node.type === 'stack' && node.id.endsWith('-brand-zone') && 'children' in node) {
      const children = node.children ?? []
      node.children = [
        {
          id: `${slot}-modgud-brand`,
          type: 'modgud-brand-header',
          name: `${slotName}ModgudBrand`,
          props: {},
        },
        ...children.filter((child) => !child.id.endsWith('-brand-mark') && !child.id.endsWith('-product-name')),
      ]
    }
    if ('children' in node && Array.isArray(node.children)) node.children.forEach(walk)
  }
  walk(schema)
  return schema
}
