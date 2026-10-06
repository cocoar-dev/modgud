import { onMounted, ref, type Ref } from 'vue'

/** What this browser can do with passkeys. */
export interface PasskeyBrowserSupport {
  /** WebAuthn is available at all (`window.PublicKeyCredential`). */
  webAuthn: boolean
  /**
   * WebAuthn Level 3 related origin requests: the browser honours an RP's
   * `/.well-known/webauthn` file, so a passkey of another domain can be offered here.
   */
  relatedOrigins: boolean
}

type ClientCapabilities = Record<string, boolean>

/** Detect passkey support once. Never throws; anything unknown reads as unsupported. */
export async function detectPasskeySupport(): Promise<PasskeyBrowserSupport> {
  const pkc = (typeof window !== 'undefined' ? window.PublicKeyCredential : undefined) as unknown as
    | { getClientCapabilities?: () => Promise<ClientCapabilities> }
    | undefined
  if (!pkc) return { webAuthn: false, relatedOrigins: false }
  try {
    const caps = await pkc.getClientCapabilities?.()
    return { webAuthn: true, relatedOrigins: caps?.relatedOrigins === true }
  } catch {
    return { webAuthn: true, relatedOrigins: false }
  }
}

/**
 * Reactive passkey support for a view. Starts as "unsupported" and fills in after
 * mount, so a passkey button never flashes up on a browser that cannot use it.
 */
export function usePasskeySupport(): Ref<PasskeyBrowserSupport> {
  const support = ref<PasskeyBrowserSupport>({ webAuthn: false, relatedOrigins: false })
  onMounted(async () => {
    support.value = await detectPasskeySupport()
  })
  return support
}
