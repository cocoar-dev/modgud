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
   * Rotate the realm's OpenIddict signing key. An ordinary rotation is scheduled (FK-03):
   * the new key is published at once and signs from `ActivatesAt`; the previous key then
   * retires into the ~30-day overlap. With `revokePrevious` it switches at once and revokes
   * the previous key. Requires `realm-settings:write`.
   */
  async function rotateSigningKey(revokePrevious = false): Promise<{ kid: string, activatesAt: string | null }> {
    const res = await http.addPath('rotate-signing-key')
      .setOptionalQueryParameter('revokePrevious', revokePrevious ? 'true' : undefined)
      .post<{ Kid: string, ActivatesAt?: string | null }>()
    return { kid: res?.Kid ?? '', activatesAt: res?.ActivatesAt ?? null }
  }

  /**
   * FK-01 — revoke every retired signing key still inside its overlap window: they stop
   * verifying anything at once (tokens they signed become invalid). For a key compromise
   * discovered after an ordinary rotation. Returns the number of keys revoked.
   */
  async function revokeRetiredSigningKeys(): Promise<number> {
    const res = await http.addPath('signing-keys').addPath('revoke-retired').post<{ Revoked: number }>()
    return res?.Revoked ?? 0
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

  return { settings, loaded, load, patch, previewPositionSecurity, rotateSigningKey, revokeRetiredSigningKeys, previewEmail }
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
