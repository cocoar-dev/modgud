import {
  definePageElement,
  type EmptyProps,
  type PageConfig,
  type PageFieldSpec,
  type PageNode,
} from '@cocoar/vue-page-builder'
import BrandHeaderElement from '@/components/page-builder/BrandHeaderElement.vue'
import BrandHeaderPreview from '@/components/page-builder/BrandHeaderPreview.vue'
import ExternalLoginsElement from '@/components/page-builder/ExternalLoginsElement.vue'
import ExternalLoginsPreview from '@/components/page-builder/ExternalLoginsPreview.vue'

export type AuthPageSlot = 'login' | 'logout' | 'password-forgot'

export const AUTH_PAGE_SLOTS: AuthPageSlot[] = ['login', 'logout', 'password-forgot']

const actionsBySlot: Record<AuthPageSlot, { id: string; label: string }[]> = {
  login: [
    { id: 'auth:login', label: 'Sign in' },
    { id: 'auth:passkey', label: 'Sign in with passkey' },
    { id: 'auth:magic-link', label: 'Open magic-link login' },
    { id: 'auth:forgot-password', label: 'Forgot password' },
    { id: 'auth:register', label: 'Create account' },
  ],
  logout: [
    { id: 'auth:back-to-login', label: 'Sign in again' },
  ],
  'password-forgot': [
    { id: 'auth:send-reset-link', label: 'Send reset link' },
    { id: 'auth:back-to-login', label: 'Back to login' },
  ],
}

const fieldsBySlot: Record<AuthPageSlot, PageFieldSpec[]> = {
  login: [
    { name: 'username', valueType: 'string', label: 'Username', required: true, defaultElement: 'text-input' },
    { name: 'password', valueType: 'string', label: 'Password', required: true, defaultElement: 'password-input' },
    { name: 'rememberMe', valueType: 'boolean', label: 'Stay signed in', defaultElement: 'checkbox' },
  ],
  logout: [],
  'password-forgot': [
    { name: 'username', valueType: 'string', label: 'Username or email', required: true, defaultElement: 'text-input' },
  ],
}

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

export function createAuthPageConfig(
  slot: AuthPageSlot,
  pickAsset?: (currentId?: string) => Promise<string | null>,
): PageConfig {
  const authElements = slot === 'login'
    ? ['modgud-brand-header', 'modgud-external-logins']
    : ['modgud-brand-header']
  return {
    allowedElements: [
      'stack', 'card', 'section', 'divider', 'spacer',
      'heading', 'paragraph', 'note',
      'text-input', 'password-input', 'checkbox', 'button', 'link', 'image',
      ...authElements,
    ],
    elements: modgudElements,
    fields: fieldsBySlot[slot],
    allowCustomFields: false,
    availableActions: actionsBySlot[slot],
    assetResolver: (id: string) => `/api/assets/${encodeURIComponent(id)}`,
    pickAsset,
  }
}

export function createDefaultAuthPageSchema(slot: AuthPageSlot): PageNode {
  if (slot === 'password-forgot') return forgotPasswordSchema()
  if (slot === 'logout') return logoutSchema()
  return loginSchema()
}

function loginSchema(): PageNode {
  return {
    id: 'login-page',
    type: 'page',
    schemaVersion: 2,
    enterSubmits: true,
    style: { minHeight: '100vh', justify: 'center', align: 'center', gap: '24px', padding: '24px' },
    children: [
      { id: 'login-brand', type: 'modgud-brand-header', props: {} },
      {
        id: 'login-card', type: 'card', props: {},
        style: { size: 'fixed', width: 'min(400px, calc(100vw - 48px))', gap: '16px', padding: '24px' },
        children: [
          { id: 'login-title', type: 'heading', props: { text: 'Sign in to continue', level: 2 } },
          { id: 'login-username', type: 'text-input', name: 'username', props: { label: 'Username', placeholder: 'Username' }, validation: { required: true } },
          { id: 'login-password', type: 'password-input', name: 'password', props: { label: 'Password', placeholder: 'Password' }, validation: { required: true } },
          { id: 'login-remember', type: 'checkbox', name: 'rememberMe', defaultValue: false, props: { label: 'Stay signed in' } },
          { id: 'login-submit', type: 'button', props: { label: 'Sign in', action: 'auth:login', validates: true, default: true }, style: { size: 'fill' } },
          { id: 'login-passkey', type: 'button', props: { label: 'Sign in with passkey', action: 'auth:passkey', variant: 'secondary' }, style: { size: 'fill' } },
          { id: 'login-providers', type: 'modgud-external-logins', props: {} },
          { id: 'login-forgot', type: 'link', props: { label: 'Forgot password?', action: 'auth:forgot-password' }, style: { alignSelf: 'center' } },
        ],
      },
    ],
  } as PageNode
}

function forgotPasswordSchema(): PageNode {
  return {
    id: 'forgot-page', type: 'page', schemaVersion: 2, enterSubmits: true,
    style: { minHeight: '100vh', justify: 'center', align: 'center', gap: '24px', padding: '24px' },
    children: [
      { id: 'forgot-brand', type: 'modgud-brand-header', props: {} },
      {
        id: 'forgot-card', type: 'card', props: {},
        style: { size: 'fixed', width: 'min(400px, calc(100vw - 48px))', gap: '16px', padding: '24px' },
        children: [
          { id: 'forgot-title', type: 'heading', props: { text: 'Reset password', level: 2 } },
          { id: 'forgot-copy', type: 'paragraph', props: { text: 'Enter your username or email address. We will send you a reset link.' } },
          { id: 'forgot-username', type: 'text-input', name: 'username', props: { label: 'Username or email' }, validation: { required: true } },
          { id: 'forgot-submit', type: 'button', props: { label: 'Send reset link', action: 'auth:send-reset-link', validates: true, default: true }, style: { size: 'fill' } },
          { id: 'forgot-back', type: 'link', props: { label: 'Back to login', action: 'auth:back-to-login' }, style: { alignSelf: 'center' } },
        ],
      },
    ],
  } as PageNode
}

function logoutSchema(): PageNode {
  return {
    id: 'logout-page', type: 'page', schemaVersion: 2,
    style: { minHeight: '100vh', justify: 'center', align: 'center', gap: '24px', padding: '24px' },
    children: [
      { id: 'logout-brand', type: 'modgud-brand-header', props: {} },
      {
        id: 'logout-card', type: 'card', props: {},
        style: { size: 'fixed', width: 'min(400px, calc(100vw - 48px))', gap: '16px', padding: '24px' },
        children: [
          { id: 'logout-title', type: 'heading', props: { text: 'Signed out', level: 2 } },
          { id: 'logout-copy', type: 'paragraph', props: { text: 'Your session has ended safely.' } },
          { id: 'logout-login', type: 'button', props: { label: 'Sign in again', action: 'auth:back-to-login' }, style: { size: 'fill' } },
        ],
      },
    ],
  } as PageNode
}
