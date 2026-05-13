import { ref } from 'vue'
import type { AssetDto, AssetInUseDto } from '@/models/assets'
import { ALLOWED_ASSET_MIME_TYPES, MAX_ASSET_SIZE_BYTES } from '@/models/assets'

/**
 * Per-realm asset library client. Wraps /api/admin/assets — multipart
 * upload uses fetch directly because the shared HttpClient sets
 * `Content-Type: application/json` unconditionally, which conflicts with
 * the boundary-driven multipart format.
 *
 * One composable instance per consumer (no shared state) — the asset
 * library is small and load-on-mount is fine; if we ever grow into the
 * thousands, a Pinia store can move in front of this.
 */
export function useAssets() {
  const assets = ref<AssetDto[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function list(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const res = await fetch('/api/admin/assets', {
        headers: { Accept: 'application/json' },
      })
      if (!res.ok) {
        error.value = `Failed to load assets (HTTP ${res.status})`
        return
      }
      assets.value = (await res.json()) as AssetDto[]
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      loading.value = false
    }
  }

  /**
   * Returns the freshly-uploaded asset on success, or a validation/
   * server error string on failure. Bytes get uploaded as-is; size and
   * MIME-type are also re-checked server-side (magic-byte sniffing,
   * SVG sanitization).
   */
  async function upload(file: File): Promise<AssetDto | { error: string }> {
    if (file.size > MAX_ASSET_SIZE_BYTES) {
      return { error: `File too large (max ${MAX_ASSET_SIZE_BYTES} bytes)` }
    }
    if (file.type && !ALLOWED_ASSET_MIME_TYPES.includes(file.type)) {
      // Soft check — server's magic-byte sniff is the real gate.
      // Browsers sometimes mislabel SVGs, so don't fail purely on this.
      // We still pass it through and let the server decide.
    }
    const form = new FormData()
    form.append('file', file, file.name)
    try {
      const res = await fetch('/api/admin/assets', {
        method: 'POST',
        body: form,
      })
      if (!res.ok) {
        let detail: string | undefined
        try {
          const body = await res.json() as { Message?: string }
          detail = body?.Message
        } catch { /* ignore */ }
        return { error: detail ?? `Upload failed (HTTP ${res.status})` }
      }
      const created = (await res.json()) as AssetDto
      // Prepend to local list so consumers see the new item immediately.
      assets.value = [created, ...assets.value]
      return created
    } catch (e) {
      return { error: e instanceof Error ? e.message : String(e) }
    }
  }

  /**
   * Server may refuse with 409 when the asset is still referenced (e.g.
   * set as the realm logo). Returns `{ inUse: ... }` in that case so the
   * caller can show the reference list.
   */
  async function remove(id: string): Promise<true | { inUse: AssetInUseDto } | { error: string }> {
    try {
      const res = await fetch(`/api/admin/assets/${encodeURIComponent(id)}`, {
        method: 'DELETE',
        headers: { Accept: 'application/json' },
      })
      if (res.status === 409) {
        const body = (await res.json()) as AssetInUseDto
        return { inUse: body }
      }
      if (!res.ok) return { error: `Delete failed (HTTP ${res.status})` }
      assets.value = assets.value.filter((a) => a.Id !== id)
      return true
    } catch (e) {
      return { error: e instanceof Error ? e.message : String(e) }
    }
  }

  return { assets, loading, error, list, upload, remove }
}
