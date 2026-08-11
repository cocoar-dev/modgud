import {
  definePageElement,
  type EmptyProps,
  type PageConfig,
} from '@cocoar/vue-page-builder'
import BrandHeaderElement from '@/components/page-builder/BrandHeaderElement.vue'
import BrandHeaderPreview from '@/components/page-builder/BrandHeaderPreview.vue'
import type { PageThemeConfig } from '@/stores/appconfig.store'
import { createAuthVisualMarkupConfig } from './authVisualMarkup'
import { authViewStates, type AuthPageLocale, type AuthPageSlot } from './authPageSlots'

/*
 * Modgud's own PageConfig for the authentication surfaces.
 *
 * Page Builder 3.0 removed `createAuthPageConfig()`: the package ships the
 * runtime and nothing auth-specific. Per its IDP_INTEGRATION.md the element,
 * action and context allowlists are the IdP's — they are the authority
 * boundary that decides what a tenant-authored document may reach, so owning
 * them here is where they belong rather than a loss.
 */

export { AUTH_PAGE_SLOTS, authPageLocale } from './authPageSlots'
export type { AuthPageLocale, AuthPageSlot }
export { createDefaultAuthPageSchema } from './authPageDocuments'

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

/** Element types a tenant document may use. Anything outside this is refused. */
const allowedElements = [
  'stack',
  'repeat',
  'card',
  'section',
  'divider',
  'spacer',
  'heading',
  'paragraph',
  'note',
  'feedback',
  'text-input',
  'password-input',
  'checkbox',
  'button',
  'link',
  'image',
  'visual-markup',
  'modgud-brand-header',
] as const

type Copy = Record<'username' | 'password' | 'remember' | 'email' | 'otpCode' | 'approvedScopes', string>

const COPY: Record<AuthPageLocale, Copy> = {
  de: {
    username: 'Benutzername',
    password: 'Passwort',
    remember: 'Angemeldet bleiben',
    email: 'E-Mail',
    otpCode: '6-stelliger Code',
    approvedScopes: 'Freigegebene Berechtigungen',
  },
  en: {
    username: 'Username',
    password: 'Password',
    remember: 'Stay signed in',
    email: 'Email',
    otpCode: '6-digit code',
    approvedScopes: 'Approved scopes',
  },
}

/** The form values the host reads back from a submitted page. */
function dataContractFor(slot: AuthPageSlot, copy: Copy): PageConfig['dataContract'] {
  switch (slot) {
    case 'login':
      return [
        { name: 'username', valueType: 'string', label: copy.username, required: true, defaultElement: 'text-input' },
        { name: 'password', valueType: 'string', label: copy.password, required: true, defaultElement: 'password-input' },
        { name: 'rememberMe', valueType: 'boolean', label: copy.remember, defaultElement: 'checkbox' },
        { name: 'email', valueType: 'string', label: copy.email, defaultElement: 'text-input' },
        { name: 'otpCode', valueType: 'string', label: copy.otpCode, defaultElement: 'otp-input' },
      ]
    case 'password-forgot':
      return [
        { name: 'username', valueType: 'string', label: copy.username, required: true, defaultElement: 'text-input' },
      ]
    case 'consent':
      return [{ name: 'approvedScopes', valueType: 'string[]', label: copy.approvedScopes }]
    case 'logout':
      return []
  }
}

/**
 * Action ids a document may wire to a button or link. Each one maps to a
 * trusted host handler; the document cannot navigate or authenticate itself.
 * Keep this in step with the per-slot allowlist in the backend's
 * PageDocumentValidator, which is the authority.
 */
function actionsFor(slot: AuthPageSlot, locale: AuthPageLocale): PageConfig['availableActions'] {
  const de = locale === 'de'
  const legal = [
    { id: 'legal:terms', label: de ? 'Nutzungsbedingungen' : 'Terms' },
    { id: 'legal:privacy', label: de ? 'Datenschutz' : 'Privacy' },
  ]

  switch (slot) {
    case 'login':
      return [
        { id: 'auth:login', label: de ? 'Anmelden' : 'Sign in' },
        { id: 'auth:passkey', label: de ? 'Mit Passkey anmelden' : 'Sign in with Passkey' },
        { id: 'auth:magic-link', label: de ? 'Anmelde-Link per E-Mail' : 'Login link via email' },
        { id: 'auth:forgot-password', label: de ? 'Passwort vergessen?' : 'Forgot password?' },
        { id: 'auth:register', label: de ? 'Registrieren' : 'Register' },
        { id: 'auth:external-provider', label: de ? 'Externer Anbieter' : 'External provider' },
        { id: 'auth:toggle-language', label: de ? 'Sprache wechseln' : 'Switch language' },
        { id: 'auth:request-login-code', label: de ? 'Anmeldecode senden' : 'Send login code' },
        { id: 'auth:verify-login-code', label: de ? 'Anmeldecode prüfen' : 'Verify login code' },
        { id: 'auth:resend-login-code', label: de ? 'Anmeldecode erneut senden' : 'Resend login code' },
        { id: 'auth:back-to-email', label: de ? 'Zurück zur E-Mail' : 'Back to email' },
        ...legal,
      ]
    case 'password-forgot':
      return [
        { id: 'auth:send-reset-link', label: de ? 'Link senden' : 'Send link' },
        { id: 'auth:back-to-login', label: de ? 'Zurück zur Anmeldung' : 'Back to login' },
        ...legal,
      ]
    case 'consent':
      return [
        { id: 'auth:consent-deny', label: de ? 'Ablehnen' : 'Deny' },
        { id: 'auth:consent-allow', label: de ? 'Zulassen' : 'Allow' },
        ...legal,
      ]
    case 'logout':
      return [
        { id: 'auth:back-to-login', label: de ? 'Erneut anmelden' : 'Sign in again' },
        ...legal,
      ]
  }
}

/**
 * Host data a document may read. `runtime.viewState` carries what
 * `config.availableStates` used to declare; its `allowedValues` is what turns
 * the condition editor's free-text box back into a dropdown.
 */
function contextFieldsFor(slot: AuthPageSlot): PageConfig['contextFields'] {
  return [
    { path: 'branding.productName', type: 'string' },
    { path: 'branding.showLegal', type: 'boolean' },
    { path: 'auth.internalLoginEnabled', type: 'boolean' },
    { path: 'auth.passwordless', type: 'boolean' },
    { path: 'auth.magicLinkEnabled', type: 'boolean' },
    { path: 'auth.registrationEnabled', type: 'boolean' },
    { path: 'auth.loginEmail', type: 'string' },
    {
      path: 'auth.externalProviders',
      type: 'array',
      itemFields: [
        { path: 'id', type: 'string' },
        { path: 'name', type: 'string' },
        { path: 'color', type: 'string' },
      ],
    },
    { path: 'consent.clientName', type: 'string' },
    { path: 'consent.clientHostname', type: 'string' },
    { path: 'consent.isDynamicallyRegistered', type: 'boolean' },
    {
      path: 'consent.requestedScopes',
      type: 'array',
      itemFields: [
        { path: 'name', type: 'string' },
        { path: 'displayName', type: 'string' },
        { path: 'description', type: 'string' },
        { path: 'required', type: 'boolean' },
      ],
    },
    { path: 'feedback.message', type: 'string' },
    { path: 'feedback.success', type: 'boolean' },
    { path: 'runtime.viewState', type: 'string', allowedValues: [...authViewStates(slot)] },
  ]
}

export function createAuthPageConfig(
  slot: AuthPageSlot,
  locale: AuthPageLocale = 'en',
  pickAsset?: (currentId?: string) => Promise<string | null>,
  pageTheme?: PageThemeConfig | null,
): PageConfig {
  const copy = COPY[locale]

  return {
    allowedElements: [
      ...allowedElements,
      ...(slot === 'login' ? (['otp-input'] as const) : []),
    ],
    allowCustomFields: false,
    dataContract: dataContractFor(slot, copy),
    availableActions: actionsFor(slot, locale),
    contextFields: contextFieldsFor(slot),
    elementTypes: modgudElements,
    locales: [
      { id: 'de', label: 'Deutsch' },
      { id: 'en', label: 'English' },
    ],
    defaultLocale: 'en',
    // Mirrors the ceiling the backend validator enforces, so an author is told
    // in the editor rather than at publish time.
    documentLimits: { maxNodes: 500, maxDepth: 30 },
    // Replaces the removed preview fixtures: the host owns the sizes, and
    // every auth surface is checked at Modgud's narrow mobile viewport too.
    previewViewports: [
      { id: 'phone', label: 'Phone · 390', width: 390, height: 844 },
      { id: 'tablet', label: 'Tablet · 768', width: 768, height: 1024 },
      { id: 'desktop', label: 'Desktop · 1280', width: 1280, height: 800 },
      { id: 'fluid', label: 'Fluid' },
    ],
    assetResolver: (id: string) => `/api/assets/${encodeURIComponent(id)}`,
    pickAsset,
    visualMarkup: createAuthVisualMarkupConfig(pageTheme ?? null),
  }
}
