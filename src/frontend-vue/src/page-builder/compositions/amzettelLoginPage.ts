import { materializePageComposition } from '@cocoar/vue-page-builder'
import type {
  ElementNode,
  PageCompositionDefinition,
  PageNode,
  PageRootNode,
} from '@cocoar/vue-page-builder'

/*
 * The amZettel login page, rebuilt as a PageBuilder document.
 *
 * This is the reference case: the real page at https://app.amzettel.at,
 * expressed entirely through the generic element, style and action contracts —
 * no auth-specific element types, no host CSS class, nothing amZettel-shaped in
 * the renderer. If this can be authored, a realm can rebuild its own login.
 *
 * The form is the passwordless flow the reference shows: an email address, a
 * mailed login code, and a passkey as the alternative. The code step is not a
 * second page but the same document reacting to `runtime.viewState`, which the
 * host sets once it has sent the code.
 */

const DE = {
  title: 'Mit Code anmelden',
  subtitle: 'Gib deine E-Mail-Adresse ein — wir senden dir einen Anmeldecode.',
  email: 'E-Mail',
  emailPlaceholder: 'du@beispiel.at',
  sendCode: 'Code senden',
  or: 'ODER',
  passkey: 'Mit Passkey anmelden',
  codeTitle: 'Code eingeben',
  codeSubtitle: 'Wir haben dir einen 6-stelligen Code geschickt.',
  code: 'Anmeldecode',
  verify: 'Anmelden',
  resend: 'Code erneut senden',
  back: 'Andere E-Mail verwenden',
  privacy: 'Datenschutz',
  legal: 'Impressum',
}

const EN = {
  title: 'Sign in with a code',
  subtitle: "Enter your email — we'll send you a login code.",
  email: 'Email',
  emailPlaceholder: 'you@example.com',
  sendCode: 'Send code',
  or: 'OR',
  passkey: 'Sign in with a passkey',
  codeTitle: 'Enter your code',
  codeSubtitle: 'We sent you a 6-digit code.',
  code: 'Login code',
  verify: 'Sign in',
  resend: 'Resend code',
  back: 'Use a different email',
  privacy: 'Privacy',
  legal: 'Legal notice',
}

/** Shows a node only while the host reports this view state. */
function whileViewState(value: string) {
  return { source: 'context' as const, path: 'runtime.viewState', operator: 'equals' as const, value }
}

function translated(key: string, fallback: string) {
  return { source: 'translation' as const, key, fallback }
}

/** The email step: address, send, and the passkey alternative below a rule. */
function emailStep(): ElementNode {
  return {
    id: 'amzettel-email-step',
    type: 'stack',
    name: 'emailStep',
    props: { direction: 'column' },
    style: { gap: '18px', size: 'fill' },
    visibleWhen: {
      any: [whileViewState('credentials'), whileViewState('passwordless'), whileViewState('submitting'), whileViewState('error')],
    },
    children: [
      {
        id: 'amzettel-title',
        type: 'heading',
        name: 'title',
        props: { level: 1, text: translated('page.title', EN.title) },
        style: { fontSize: 'xlarge', foreground: 'primary' },
      },
      {
        id: 'amzettel-subtitle',
        type: 'paragraph',
        name: 'subtitle',
        props: { text: translated('page.subtitle', EN.subtitle) },
        style: { foreground: 'secondary', fontSize: 'small' },
      },
      {
        id: 'amzettel-email',
        type: 'text-input',
        name: 'email',
        props: {
          label: translated('page.email', EN.email),
          placeholder: translated('page.emailPlaceholder', EN.emailPlaceholder),
        },
        style: { size: 'fill' },
      },
      {
        id: 'amzettel-send-code',
        type: 'button',
        name: 'sendCode',
        props: { label: translated('page.sendCode', EN.sendCode), action: 'auth:request-login-code', variant: 'primary' },
        style: { size: 'fill' },
      },
      {
        id: 'amzettel-divider',
        type: 'stack',
        name: 'divider',
        props: { direction: 'row' },
        style: { gap: '12px', align: 'center', size: 'fill' },
        children: [
          { id: 'amzettel-divider-left', type: 'divider', name: 'dividerLeft', props: {}, style: { size: 'fill' } },
          {
            id: 'amzettel-divider-label',
            type: 'paragraph',
            name: 'dividerLabel',
            props: { text: translated('page.or', EN.or) },
            style: { foreground: 'tertiary', fontSize: 'caption', size: 'fit' },
          },
          { id: 'amzettel-divider-right', type: 'divider', name: 'dividerRight', props: {}, style: { size: 'fill' } },
        ],
      },
      {
        id: 'amzettel-passkey',
        type: 'button',
        name: 'passkey',
        props: { label: translated('page.passkey', EN.passkey), action: 'auth:passkey', variant: 'secondary' },
        style: { size: 'fill' },
      },
    ],
  }
}

/** The code step, shown once the host has mailed a code. */
function codeStep(): ElementNode {
  return {
    id: 'amzettel-code-step',
    type: 'stack',
    name: 'codeStep',
    props: { direction: 'column' },
    style: { gap: '18px', size: 'fill' },
    visibleWhen: whileViewState('login-code'),
    children: [
      {
        id: 'amzettel-code-title',
        type: 'heading',
        name: 'codeTitle',
        props: { level: 1, text: translated('page.codeTitle', EN.codeTitle) },
        style: { fontSize: 'xlarge', foreground: 'primary' },
      },
      {
        id: 'amzettel-code-subtitle',
        type: 'paragraph',
        name: 'codeSubtitle',
        props: { text: translated('page.codeSubtitle', EN.codeSubtitle) },
        style: { foreground: 'secondary', fontSize: 'small' },
      },
      {
        id: 'amzettel-code',
        type: 'otp-input',
        name: 'otpCode',
        props: { label: translated('page.code', EN.code) },
        style: { size: 'fill' },
      },
      {
        id: 'amzettel-verify',
        type: 'button',
        name: 'verify',
        props: { label: translated('page.verify', EN.verify), action: 'auth:verify-login-code', variant: 'primary' },
        style: { size: 'fill' },
      },
      {
        id: 'amzettel-resend',
        type: 'link',
        name: 'resend',
        props: { label: translated('page.resend', EN.resend), action: 'auth:resend-login-code' },
        style: { foreground: 'primary', fontSize: 'small', size: 'fill' },
      },
      {
        id: 'amzettel-back',
        type: 'link',
        name: 'back',
        props: { label: translated('page.back', EN.back), action: 'auth:back-to-email' },
        style: { foreground: 'tertiary', fontSize: 'caption', size: 'fill' },
      },
    ],
  }
}

/** The white card: whichever step applies, plus feedback and the DE/EN switch. */
function formCard(): ElementNode {
  return {
    id: 'amzettel-card',
    type: 'card',
    name: 'loginCard',
    props: {},
    style: { size: 'fill', maxWidth: '420px', padding: '32px', gap: '18px', radius: 'large', elevation: 'small' },
    children: [
      emailStep(),
      codeStep(),
      {
        id: 'amzettel-context-error',
        type: 'note',
        name: 'contextError',
        props: { variant: 'error', text: '' },
        bindings: { text: { source: 'context', path: 'feedback.message' } },
        visibleWhen: { source: 'context', path: 'feedback.message', operator: 'isNotEmpty' },
      },
      {
        id: 'amzettel-action-error',
        type: 'feedback',
        name: 'actionError',
        props: { kind: 'form-error' },
      },
      {
        id: 'amzettel-language',
        type: 'link',
        name: 'languageSwitcher',
        props: { label: translated('page.languageSwitcher', 'DE'), action: 'auth:toggle-language' },
        style: { foreground: 'tertiary', fontSize: 'caption', size: 'fit' },
      },
    ],
  }
}

/** Privacy · Legal notice, under the card as on the reference. */
function legalRow(): ElementNode {
  return {
    id: 'amzettel-legal',
    type: 'stack',
    name: 'legal',
    props: { direction: 'row' },
    style: { gap: '10px', justify: 'center', align: 'center', size: 'fill' },
    visibleWhen: { source: 'context', path: 'branding.showLegal', operator: 'equals', value: true },
    children: [
      {
        id: 'amzettel-privacy',
        type: 'link',
        name: 'privacy',
        props: { label: translated('page.privacy', EN.privacy), action: 'legal:privacy' },
        style: { foreground: 'tertiary', fontSize: 'caption', size: 'fit' },
      },
      {
        id: 'amzettel-legal-separator',
        type: 'paragraph',
        name: 'legalSeparator',
        props: { text: '·' },
        style: { foreground: 'tertiary', fontSize: 'caption', size: 'fit' },
      },
      {
        id: 'amzettel-imprint',
        type: 'link',
        name: 'imprint',
        props: { label: translated('page.legal', EN.legal), action: 'legal:terms' },
        style: { foreground: 'tertiary', fontSize: 'caption', size: 'fit' },
      },
    ],
  }
}

/**
 * Builds the document. `composition` must be the definition as the server
 * stored it, so the materialized nodes pin the id and version that actually
 * exist in this realm.
 */
export function createAmzettelLoginDocument(composition: PageCompositionDefinition): PageNode {
  const root: PageRootNode = {
    id: 'amzettel-login',
    type: 'page',
    schemaVersion: 6,
    // No size on the root — the host container owns the box (3.0).
    style: { padding: '0', surface: 'default', align: 'stretch', justify: 'start' },
    translations: {
      de: {
        'page.title': DE.title,
        'page.subtitle': DE.subtitle,
        'page.email': DE.email,
        'page.emailPlaceholder': DE.emailPlaceholder,
        'page.sendCode': DE.sendCode,
        'page.or': DE.or,
        'page.passkey': DE.passkey,
        'page.codeTitle': DE.codeTitle,
        'page.codeSubtitle': DE.codeSubtitle,
        'page.code': DE.code,
        'page.verify': DE.verify,
        'page.resend': DE.resend,
        'page.back': DE.back,
        'page.privacy': DE.privacy,
        'page.legal': DE.legal,
        'page.languageSwitcher': 'EN',
      },
      en: {
        'page.title': EN.title,
        'page.subtitle': EN.subtitle,
        'page.email': EN.email,
        'page.emailPlaceholder': EN.emailPlaceholder,
        'page.sendCode': EN.sendCode,
        'page.or': EN.or,
        'page.passkey': EN.passkey,
        'page.codeTitle': EN.codeTitle,
        'page.codeSubtitle': EN.codeSubtitle,
        'page.code': EN.code,
        'page.verify': EN.verify,
        'page.resend': EN.resend,
        'page.back': EN.back,
        'page.privacy': EN.privacy,
        'page.legal': EN.legal,
        'page.languageSwitcher': 'DE',
      },
    },
    children: [],
  }

  const panel = materializePageComposition(composition, { page: root })

  const formPane: ElementNode = {
    id: 'amzettel-form-pane',
    type: 'stack',
    name: 'formPane',
    props: { direction: 'column' },
    style: {
      size: 'fill',
      minWidth: '0',
      padding: '48px 32px',
      gap: '20px',
      justify: 'center',
      align: 'center',
      surface: 'subtle',
    },
    children: [formCard(), legalRow()],
  }

  // The shell sits in the page's column flow, where stretch only governs width.
  // Taking the page's full height is a main-axis concern, so it needs grow
  // rather than a percentage height.
  const shell: ElementNode = {
    id: 'amzettel-shell',
    type: 'stack',
    name: 'shell',
    props: { direction: 'row' },
    style: { size: 'grow', minWidth: '0', gap: '0', align: 'stretch' },
    children: [panel, formPane],
  }

  root.children = [shell]
  return root
}
