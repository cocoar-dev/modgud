import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth.store'

/**
 * Whether a widget that reads from the administration (`/api/admin/*`) has to wait: the
 * dashboard is part of the portal and opens at the realm's sign-in level, the
 * administration may require a higher one (ADR 0025). Below it such a widget shows the
 * confirmation (`WidgetCard`'s `locked`) and requests nothing — the refusal would pull the
 * user off the dashboard to the login page.
 */
export function useAdministrationLock() {
  const authStore = useAuthStore()
  return computed(() => authStore.user?.AdministrationNeedsStepUp === true)
}
