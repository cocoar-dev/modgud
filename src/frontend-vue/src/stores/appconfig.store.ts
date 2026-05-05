import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'

/**
 * UI configuration from the server — loaded once at startup. Sourced from
 * the anonymous /api/app-info endpoint, which RealmMiddleware has already
 * resolved to a tenant by the time it returns: IsControlPlane reflects the
 * realm of the host the SPA was loaded from.
 */
export interface AppConfig {
  AuthenticationMinimumLevel: number  // 0=None, 1=SecureLogin, 2=Passwordless
  MagicLinkSelfService: boolean
  TwoFactorGracePeriodDays: number
  IsControlPlane: boolean              // true ⇔ the realm hosting this SPA is the Control Plane
}

const defaults: AppConfig = {
  AuthenticationMinimumLevel: 1,
  MagicLinkSelfService: true,
  TwoFactorGracePeriodDays: 14,
  IsControlPlane: false,
}

export const useAppConfigStore = defineStore('appConfig', () => {
  const http = useHttpClient('/api/app-info')
  const config = ref<AppConfig>({ ...defaults })
  const loaded = ref(false)

  async function load() {
    if (loaded.value) return
    try {
      const result = await http.get<AppConfig>()
      if (result) config.value = { ...defaults, ...result }
    } catch { /* use defaults */ }
    finally { loaded.value = true }
  }

  return { config, loaded, load }
})
