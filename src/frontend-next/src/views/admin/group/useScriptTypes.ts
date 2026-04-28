/**
 * Fetches the Monaco <c>.d.ts</c> for script authoring from the backend.
 * The entire definition — domain types, UserContext, linq.* helpers, globals —
 * is generated server-side from C# reflection + registered JsEval modules.
 * Nothing here is handwritten.
 */
import { ref } from 'vue'

// Module-level cache: fetched once per page load, shared across every editor instance.
const sharedTypeDefinitions = ref<string>('')
let loadPromise: Promise<void> | null = null

async function loadOnce(): Promise<void> {
  if (loadPromise) return loadPromise
  loadPromise = (async () => {
    const res = await fetch('/api/script-types/principal')
    if (!res.ok) {
      console.error('[script-types] fetch failed', res.status, await res.text().catch(() => ''))
      return
    }
    sharedTypeDefinitions.value = await res.text()
  })()
  return loadPromise
}

export function useScriptTypes() {
  if (!loadPromise) void loadOnce()
  return { sharedTypeDefinitions }
}
