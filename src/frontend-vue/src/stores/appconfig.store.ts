import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'

/**
 * UI configuration from the server — loaded once at startup. Sourced from
 * the anonymous /api/app-info endpoint, which RealmMiddleware has already
 * resolved to a tenant by the time it returns: IsControlPlane reflects the
 * realm of the host the SPA was loaded from.
 */
export interface BrandingConfig {
  ProductName: string | null
  LogoUrl: string | null
  FaviconUrl: string | null
  PrimaryColor: string | null
}

/**
 * Operator-level feature toggles. System-wide (set in configuration.json /
 * ENV), not per-tenant. Source of truth is the backend AppSettings.Features
 * block. Defaults below are the SAFE-OFF state — if /api/app-info ever fails
 * the SPA falls back to "everything dark".
 */
export interface FeatureFlags {
  PageBuilder: boolean
}

/**
 * Resolved (App⊕realm) required-identity-field policy for the host the SPA was
 * loaded from. Email is always 'Required' (the anchor); the others are one of
 * 'Off' | 'Optional' | 'Required'. Drives which inputs forms render and require.
 */
export type FieldRequirement = 'Off' | 'Optional' | 'Required'

export interface RegistrationFieldsConfig {
  Email: FieldRequirement
  Username: FieldRequirement
  Firstname: FieldRequirement
  Lastname: FieldRequirement
}

export interface AppConfig {
  AuthenticationMinimumLevel: number  // 0=None, 1=SecureLogin, 2=Passwordless
  MagicLinkSelfService: boolean
  TwoFactorGracePeriodDays: number
  IsControlPlane: boolean              // true ⇔ the realm hosting this SPA is the Control Plane
  Branding: BrandingConfig
  Features: FeatureFlags
  RegistrationFields: RegistrationFieldsConfig
  /** Effective PageBuilder schemas for the current Host/OAuth client context. */
  Pages: Record<string, string>
}

const defaults: AppConfig = {
  AuthenticationMinimumLevel: 1,
  MagicLinkSelfService: true,
  TwoFactorGracePeriodDays: 14,
  IsControlPlane: false,
  Branding: {
    ProductName: null,
    LogoUrl: null,
    FaviconUrl: null,
    PrimaryColor: null,
  },
  Features: {
    PageBuilder: false,
  },
  // Lenient default = today's behaviour, used until /api/app-info responds.
  RegistrationFields: {
    Email: 'Required',
    Username: 'Optional',
    Firstname: 'Optional',
    Lastname: 'Optional',
  },
  Pages: {},
}

/**
 * Applies per-realm branding to the document at SPA boot. Sets
 * --coar-color-primary CSS variable, document title prefix, and rewrites
 * the favicon link element. Falls back silently when a branding field is
 * null — the design-system defaults stay in effect.
 *
 * Logo + ProductName are read by views that render them (header / login)
 * via the store directly — DOM-side this function only handles globals.
 */
function applyBranding(branding: BrandingConfig): void {
  if (branding.PrimaryColor) {
    document.documentElement.style.setProperty('--coar-color-primary', branding.PrimaryColor)
  }

  if (branding.ProductName) {
    document.title = branding.ProductName
  }

  if (branding.FaviconUrl) {
    let link = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
    if (!link) {
      link = document.createElement('link')
      link.rel = 'icon'
      document.head.appendChild(link)
    }
    link.href = branding.FaviconUrl
  }
}

export const useAppConfigStore = defineStore('appConfig', () => {
  const http = useHttpClient('/api/app-info')
  const config = ref<AppConfig>({ ...defaults })
  const loaded = ref(false)

  async function fetchConfig(returnUrl?: string) {
    try {
      const request = returnUrl
        ? http.setQueryParameter('returnUrl', returnUrl)
        : http
      const result = await request.get<AppConfig>()
      if (result) {
        config.value = {
          ...defaults,
          ...result,
          Branding: { ...defaults.Branding, ...(result.Branding ?? {}) },
          Features: { ...defaults.Features, ...(result.Features ?? {}) },
          RegistrationFields: { ...defaults.RegistrationFields, ...(result.RegistrationFields ?? {}) },
          Pages: { ...(result.Pages ?? {}) },
        }
        applyBranding(config.value.Branding)
      }
    } catch { /* use defaults */ }
    finally { loaded.value = true }
  }

  async function load() {
    if (loaded.value) return
    await fetchConfig()
  }

  /** Refresh presentation for a local /connect/authorize continuation. */
  async function loadForLogin(returnUrl: string) {
    await fetchConfig(returnUrl)
  }

  return { config, loaded, load, loadForLogin }
})
