import type { ComputedRef, InjectionKey, Ref } from 'vue'
import { computed, inject, ref } from 'vue'
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
  // CoarPageBuilder renders custom elements inside its own editor preview,
  // outside LoginView. Neutral fallback data keeps those elements previewable
  // without granting the editor any real login actions.
  return inject(LOGIN_PAGE_RUNTIME_KEY, {
    branding: computed(() => ({
      ProductName: null,
      LogoUrl: null,
      FaviconUrl: null,
      PrimaryColor: null,
    })),
    externalLogins: ref([]),
    startExternalLogin: () => {},
  })
}
