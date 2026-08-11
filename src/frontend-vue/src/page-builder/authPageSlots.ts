/*
 * Page Builder 3.0 ships nothing auth-specific — no `AuthPageSlot`, no
 * `AuthPageLocale`, no starting documents. The IdP owns them, which is the
 * right boundary: these names describe Modgud's authentication surfaces, not
 * a generic page-authoring concern.
 *
 * Keep them here rather than in authPageConfig so the config and the starting
 * documents can both depend on them without a cycle.
 */

/** The authentication views a realm may customise. */
export type AuthPageSlot = 'login' | 'password-forgot' | 'logout' | 'consent'

export type AuthPageLocale = 'de' | 'en'

export const AUTH_PAGE_SLOTS: AuthPageSlot[] = [
  'login',
  'password-forgot',
  'logout',
  'consent',
]

export function authPageLocale(language: string): AuthPageLocale {
  return language.toLowerCase().startsWith('de') ? 'de' : 'en'
}

/**
 * The view states the host drives for a slot. These are ordinary host data:
 * the page reads them through `context.runtime.viewState`. Declaring them as
 * that field's closed value set is what gives the condition editor a dropdown
 * — the job `config.availableStates` used to do before 3.0 removed it as a
 * second mechanism for something `runtimeContext` already carried.
 */
export function authViewStates(slot: AuthPageSlot): readonly string[] {
  switch (slot) {
    case 'login':
      return [
        'credentials',
        'passwordless',
        'magic-link-sent',
        'login-code',
        'submitting',
        'error',
        'mfa-continuation',
      ]
    case 'password-forgot':
      return ['form', 'submitting', 'accepted', 'passwordless-unavailable', 'error']
    case 'consent':
      return ['loading', 'prompt', 'submitting', 'denied', 'expired', 'forbidden', 'error']
    case 'logout':
      return ['complete', 'federated-complete', 'provider-error']
  }
}
