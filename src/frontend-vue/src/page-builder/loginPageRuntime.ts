import type { ComputedRef, InjectionKey, Ref } from 'vue'
import { inject } from 'vue'
import type { BrandingConfig } from '@/stores/appconfig.store'

export interface ExternalLoginDto {
  Id: string
  Kind: string
  Slug: string
  DisplayName: string
  Flavor: string
  IconName?: string | null
  ButtonColorHex?: string | null
}

export interface LoginPageRuntimeContext {
  branding: ComputedRef<BrandingConfig>
  externalLogins: Ref<ExternalLoginDto[]>
  startExternalLogin: (provider: ExternalLoginDto) => void
}

export const LOGIN_PAGE_RUNTIME_KEY: InjectionKey<LoginPageRuntimeContext>
  = Symbol.for('modgud.login-page-runtime') as InjectionKey<LoginPageRuntimeContext>

export function useLoginPageRuntime(): LoginPageRuntimeContext {
  const context = inject(LOGIN_PAGE_RUNTIME_KEY)
  if (!context) throw new Error('Login PageBuilder element rendered outside LoginView.')
  return context
}
