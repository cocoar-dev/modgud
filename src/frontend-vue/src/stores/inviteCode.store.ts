import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useSignalR } from '@/composables/useSignalR'
import type {
  InviteCodeDto,
  MintInviteCodesDto,
  MintInviteCodesResultDto,
} from '@/models/inviteCode'

interface InviteCodeDataEvent {
  Subject: string
  Action: 'Created' | 'Updated' | 'Deleted' | 'Custom' | 'FullSync'
  /** Payload entries carry at least an AppId so the grid can reload selectively. */
  Payload: { AppId?: string }[]
}

/**
 * ADR-0012 invite-code store. Unlike the realm-wide grids, invite codes are
 * strictly app-scoped (`/api/app/{appId}/invite-codes`), so the store tracks a
 * `selectedAppId` the list view drives via its app picker and the bulk-mint
 * modal reads to know which app to mint for. Live-updated via the
 * InviteCodeActions SignalR stream (realm-scoped); an event whose AppId matches
 * the app currently shown triggers a reload, so codes minted/revoked out-of-band
 * (M2M backend, another admin/tab) appear without a manual refresh.
 */
export const useInviteCodeStore = defineStore('invite-code', () => {
  const http = useHttpClient('/api/app')
  const signalr = useSignalR()

  const codes = ref<InviteCodeDto[]>([])
  const loadedAppId = ref<string | null>(null)
  /** The app the list/modal currently operate on (an App.Id), or null. */
  const selectedAppId = ref<string | null>(null)
  let subscribed = false

  function setApp(appId: string | null) {
    selectedAppId.value = appId
  }

  function initialize() {
    if (subscribed) return
    subscribed = true
    // (Re)subscribe + re-sync on every (re)connect, de-duped by the stream key.
    signalr.runOnEveryReconnect(() => {
      subscribeToSignalR()
      void refresh()
    }, 'InviteCodeActions.Subscribe')
  }

  function subscribeToSignalR() {
    signalr.stream<InviteCodeDataEvent>('InviteCodeActions.Subscribe').subscribe({
      next: (ev) => {
        const appId = loadedAppId.value
        if (!appId) return
        // Reload only when the event concerns the app the grid is showing.
        const concernsThisApp = ev.Payload.some((p) => p.AppId === appId)
        if (concernsThisApp) void loadForApp(appId)
      },
      error: (err) => console.error('[invite-code] SignalR stream error:', err),
    })
  }

  async function loadForApp(appId: string): Promise<InviteCodeDto[]> {
    const res = await http.addPath(appId, 'invite-codes').get<InviteCodeDto[]>()
    codes.value = res
    loadedAppId.value = appId
    return res
  }

  async function refresh(): Promise<void> {
    if (selectedAppId.value) await loadForApp(selectedAppId.value)
  }

  async function mint(appId: string, dto: MintInviteCodesDto): Promise<MintInviteCodesResultDto> {
    const result = await http.addPath(appId, 'invite-codes').post<MintInviteCodesResultDto>(dto)
    // The mint response carries only plaintext; reload to surface the new rows
    // (metadata) in the grid the admin is looking at.
    if (loadedAppId.value === appId) await loadForApp(appId)
    return result
  }

  async function revoke(appId: string, id: string): Promise<void> {
    await http.addPath(appId, 'invite-codes', id).delete()
    codes.value = codes.value.filter((c) => c.Id !== id)
  }

  return {
    codes,
    loadedAppId,
    selectedAppId,
    setApp,
    initialize,
    loadForApp,
    refresh,
    mint,
    revoke,
  }
})
