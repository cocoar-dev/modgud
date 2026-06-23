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
  Payload: unknown[]
}

/**
 * ADR-0012 invite-code store. The admin grid loads the realm-wide list once
 * (`GET /api/admin/invite-codes` — every app's codes) and filters client-side by
 * the shared header App selector, the same shape as the Clients / Scopes grids.
 * Minting and revoking stay app-scoped (the OAuth scope is app-bound), so those
 * still go through `/api/app/{appId}/invite-codes`. `selectedAppId` is synced
 * from the header App context so the bulk-mint modal knows its target app (null
 * on 'all' / 'global' → minting disabled). Live via the InviteCodeActions
 * SignalR stream (realm-scoped); any event reloads the list.
 */
export const useInviteCodeStore = defineStore('invite-code', () => {
  const appHttp = useHttpClient('/api/app')
  const adminHttp = useHttpClient('/api/admin/invite-codes')
  const signalr = useSignalR()

  const codes = ref<InviteCodeDto[]>([])
  const loaded = ref(false)
  const selectedAppId = ref<string | null>(null)
  let subscribed = false

  function setApp(appId: string | null) {
    selectedAppId.value = appId
  }

  async function loadAll(): Promise<InviteCodeDto[]> {
    const res = await adminHttp.get<InviteCodeDto[]>()
    codes.value = res
    loaded.value = true
    return res
  }

  function refresh(): Promise<InviteCodeDto[]> {
    return loadAll()
  }

  function initialize() {
    if (!subscribed) {
      subscribed = true
      // (Re)subscribe + re-sync on every (re)connect; codes minted/revoked
      // out-of-band (M2M backend, another admin/tab) appear without a manual reload.
      signalr.runOnEveryReconnect(() => {
        subscribeToSignalR()
        void loadAll()
      }, 'InviteCodeActions.Subscribe')
    }
    if (!loaded.value) void loadAll()
  }

  function subscribeToSignalR() {
    signalr.stream<InviteCodeDataEvent>('InviteCodeActions.Subscribe').subscribe({
      next: () => void loadAll(),
      error: (err) => console.error('[invite-code] SignalR stream error:', err),
    })
  }

  async function mint(appId: string, dto: MintInviteCodesDto): Promise<MintInviteCodesResultDto> {
    const result = await appHttp.addPath(appId, 'invite-codes').post<MintInviteCodesResultDto>(dto)
    await loadAll()
    return result
  }

  async function revoke(appId: string, id: string): Promise<void> {
    await appHttp.addPath(appId, 'invite-codes', id).delete()
    codes.value = codes.value.filter((c) => c.Id !== id)
  }

  return {
    codes,
    loaded,
    selectedAppId,
    setApp,
    loadAll,
    refresh,
    initialize,
    mint,
    revoke,
  }
})
