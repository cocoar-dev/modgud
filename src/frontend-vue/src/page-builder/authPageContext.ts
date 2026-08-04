import type { AppConfig } from '@/stores/appconfig.store'
import type { ExternalLoginDto } from '@/page-builder/loginPageRuntime'

export function createAuthRuntimeContext(options: {
  config: AppConfig
  externalProviders?: ExternalLoginDto[]
  registrationEnabled?: boolean
  viewState: string
  feedbackMessage?: string
  feedbackSuccess?: boolean
  consent?: Record<string, unknown>
}): Record<string, unknown> {
  const { config } = options
  return {
    branding: {
      productName: config.Branding.ProductName ?? 'Modgud',
      showLegal: !!(config.Legal.TermsOfServiceUrl || config.Legal.PrivacyPolicyUrl),
    },
    auth: {
      internalLoginEnabled: config.InternalLoginEnabled,
      passwordless: config.AuthenticationMinimumLevel >= 2,
      magicLinkEnabled: config.MagicLinkSelfService,
      registrationEnabled: options.registrationEnabled === true,
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
