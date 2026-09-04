import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type { RealmSettingsDto, UpdateRealmSettingsDto, UpdatePositionSecuritySettingsDto, PositionSecurityConsequencesDto } from '@/models/realmSettings'

/**
 * Realm-wide settings store. One singleton doc per tenant DB — the
 * current realm is implicit from the host. GET returns defaults when
 * the doc has never been written; PATCH lazy-creates on first write.
 *
 * <para>Permissions: `realm-settings:read` / `:write`. Realm-admin
 * (with `realm:admin` bypass) holds both.</para>
 */
export const useRealmSettingsStore = defineStore('realmSettings', () => {
  const http = useHttpClient('/api/admin/realm-settings')

  const settings = ref<RealmSettingsDto | null>(null)
  const loaded = ref(false)

  async function load(): Promise<RealmSettingsDto> {
    const dto = await http.get<RealmSettingsDto>()
    settings.value = dto
    loaded.value = true
    return dto
  }

  async function patch(dto: UpdateRealmSettingsDto): Promise<RealmSettingsDto> {
    const updated = await http.patch<RealmSettingsDto>(dto)
    settings.value = updated
    return updated
  }

  /**
   * Rotate the realm's OpenIddict signing key. The previous active key is
   * retired into a ~30-day verification overlap window so in-flight tokens
   * stay valid. Requires `realm-settings:write`. Returns the new active key id.
   */
  async function rotateSigningKey(): Promise<string> {
    const res = await http.addPath('rotate-signing-key').post<{ Kid: string }>()
    return res?.Kid ?? ''
  }

  async function previewPositionSecurity(
    dto: UpdatePositionSecuritySettingsDto,
  ): Promise<PositionSecurityConsequencesDto> {
    return http.addPath('position-security').addPath('preview')
      .post<PositionSecurityConsequencesDto>(dto)
  }

  /**
   * Renders a built-in transactional template exactly as it would be sent — the
   * real template store with the effective branding, overlaid with unsaved form
   * values. Drives the tabbed EmailPreview component.
   */
  async function previewEmail(request: EmailPreviewRequest): Promise<EmailPreviewResult> {
    return http.addPath('email-preview').post<EmailPreviewResult>(request)
  }

  return { settings, loaded, load, patch, previewPositionSecurity, rotateSigningKey, previewEmail }
})

/** Unsaved form values overlaid on the effective branding for the preview. */
export interface EmailPreviewOverlay {
  productName?: string | null
  primaryColor?: string | null
  logoUrl?: string | null
  branding?: {
    productName?: string | null
    subjectPrefix?: string | null
    preheader?: string | null
    footerText?: string | null
    fromName?: string | null
    fromAddress?: string | null
    replyTo?: string | null
  } | null
}

export interface EmailPreviewRequest extends EmailPreviewOverlay {
  template: string
  language?: 'de' | 'en'
  applicationId?: string
}

export interface EmailPreviewResult {
  Template: string
  Subject: string
  From: string
  ReplyTo: string | null
  HtmlBody: string
  TextBody: string
}
