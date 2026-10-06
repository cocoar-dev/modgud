import type { AppConfig } from '@/stores/appconfig.store'
import type { ExternalLoginDto } from '@/page-builder/loginPageRuntime'
import type { PasskeyBrowserSupport } from '@/composables/usePasskeySupport'

export function createAuthRuntimeContext(options: {
  config: AppConfig
  externalProviders?: ExternalLoginDto[]
  registrationEnabled?: boolean
  loginEmail?: string
  viewState: string
  feedbackMessage?: string
  feedbackSuccess?: boolean
  consent?: Record<string, unknown>
  /** What this browser can do with passkeys; unknown (still detecting) reads as "no". */
  passkeySupport?: PasskeyBrowserSupport
}): Record<string, unknown> {
  const { config } = options
  const support = options.passkeySupport ?? { webAuthn: false, relatedOrigins: false }
  return {
    branding: {
      productName: config.Branding.ProductName ?? 'Modgud',
      showLegal: !!(config.Legal.TermsOfServiceUrl || config.Legal.PrivacyPolicyUrl),
    },
    auth: {
      internalLoginEnabled: config.InternalLoginEnabled,
      passwordless: !config.SignIn.Password,
      passwordEnabled: config.SignIn.Password,
      emailCodeEnabled: config.SignIn.EmailCode,
      passkeyEnabled: config.SignIn.Passkey,
      passkeySupported: support.webAuthn,
      passkeyRelatedOriginsSupported: support.relatedOrigins,
      passkeyAvailable: isPasskeyAvailable(config, support),
      magicLinkEnabled: config.MagicLinkSelfService,
      registrationEnabled: options.registrationEnabled === true,
      loginEmail: options.loginEmail ?? '',
      externalProviders: (options.externalProviders ?? []).map(provider => ({
        id: provider.Id,
        name: provider.DisplayName,
        color: provider.ButtonColorHex ?? '',
      })),
    },
    consent: options.consent ?? {
      clientName: '',
      clientHostname: '',
      isDynamicallyRegistered: false,
      requestedScopes: [],
    },
    feedback: {
      message: options.feedbackMessage ?? '',
      success: options.feedbackSuccess === true,
    },
    runtime: { viewState: options.viewState },
  }
}

/**
 * Whether a passkey button can do anything on this page: the target app allows
 * passkeys, the browser has WebAuthn, and — when the app's passkeys reach this page
 * only through related origins — the browser supports those too.
 */
export function isPasskeyAvailable(config: AppConfig, support: PasskeyBrowserSupport): boolean {
  if (!config.InternalLoginEnabled || !config.SignIn.Passkey || !support.webAuthn) return false
  return !config.SignIn.PasskeyNeedsRelatedOrigins || support.relatedOrigins
}
