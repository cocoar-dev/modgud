import {
  createAuthPageConfig as createPresetConfig,
  createAuthPageDocument,
  definePageElement,
  type AuthPageLocale,
  type AuthPageSlot,
  type EmptyProps,
  type PageConfig,
  type PageNode,
  type PagePreviewFixture,
  type PageRootNode,
} from '@cocoar/vue-page-builder'
import BrandHeaderElement from '@/components/page-builder/BrandHeaderElement.vue'
import BrandHeaderPreview from '@/components/page-builder/BrandHeaderPreview.vue'
import ExternalLoginsElement from '@/components/page-builder/ExternalLoginsElement.vue'
import ExternalLoginsPreview from '@/components/page-builder/ExternalLoginsPreview.vue'

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
  // Kept for schema-v2 variants saved before the generic repeat element was
  // introduced. New defaults use auth.externalProviders through `repeat`.
  'modgud-external-logins': definePageElement<EmptyProps>({
    renderer: ExternalLoginsElement,
    builder: {
      label: { key: 'modgud.pageBuilder.externalLogins', fallback: 'External login providers' },
      icon: 'log-in',
      defaults: () => ({}),
      preview: ExternalLoginsPreview,
    },
  }),
}

export function authPageLocale(language: string): AuthPageLocale {
  return language.toLowerCase().startsWith('de') ? 'de' : 'en'
}

export function createAuthPageConfig(
  slot: AuthPageSlot,
  locale: AuthPageLocale = 'en',
  pickAsset?: (currentId?: string) => Promise<string | null>,
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
    // Fixtures are a host-owned acceptance contract. Exercise every auth data
    // shape at both a desktop breakpoint and Modgud's narrow mobile viewport.
    previewFixtures,
    allowedElements: [
      ...(preset.allowedElements ?? []),
      'modgud-brand-header',
      'modgud-external-logins',
    ],
    elements: { ...(preset.elements ?? {}), ...modgudElements },
    assetResolver: (id: string) => `/api/assets/${encodeURIComponent(id)}`,
    pickAsset,
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

  // Keep viewport ownership in the page document itself. This makes the
  // built-in template behave identically in the editor preview and in the
  // auth host, while still letting administrators change the page shell via
  // Root Page Code without replacing the layout.
  if (schema.type === 'page') {
    (schema as PageRootNode).rootCode = `definePageRoot({
  compute(page) {
    page.style.minHeight = '100dvh'
    page.style.width = '100%'
  },
})`
  }

  const walk = (node: PageNode) => {
    if (node.type === 'stack' && node.id.endsWith('-brand-zone') && 'children' in node) {
      const children = node.children ?? []
      node.children = [
        { id: `${slot}-modgud-brand`, type: 'modgud-brand-header', props: {} },
        ...children.filter((child) => !child.id.endsWith('-brand-mark') && !child.id.endsWith('-product-name')),
      ]
    }
    if ('children' in node && Array.isArray(node.children)) node.children.forEach(walk)
  }
  walk(schema)
  return schema
}
