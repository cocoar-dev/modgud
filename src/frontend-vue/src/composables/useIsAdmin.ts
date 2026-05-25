import { computed, type ComputedRef } from 'vue'
import { useAuthStore } from '@/stores/auth.store'

/**
 * Resource permissions that gate any admin-area sidebar item. Mirrors the list
 * in `views/admin/AdminView.vue`. If the user holds any of these (directly or
 * via the `realm:admin` / `<resource>:admin` bypasses implemented in
 * `authStore.hasPermission`), they get the admin face of the dashboard.
 *
 * Kept in one place so the dashboard and the sidebar can't drift apart: when
 * a new admin resource is added, both pick it up by editing this list.
 *
 * Strings are bare 2-segment `<resource>:<action>` form — the App context
 * (modgud, with control-plane realm:read mixed in) is implicit.
 * `authStore.hasPermission` evaluates against the modgud grant set
 * by default and falls through to realm:admin for everything.
 */
export const ADMIN_PERMISSIONS: readonly string[] = [
  'user:read',
  'permission-role:read',
  'authorization-group:read',
  'login-provider:read',
  'oauth-client:read',
  'oauth-scope:read',
  'oauth-api:read',
  'app:read',
  'realm:read',
  'auth-log:read',
  'user:write',
  'realm:admin',
] as const

/**
 * `true` iff the current user holds at least one admin-area permission.
 * Reactive — flips back to `false` after logout / permission revocation.
 */
export function useIsAdmin(): ComputedRef<boolean> {
  const authStore = useAuthStore()
  return computed(() => ADMIN_PERMISSIONS.some((p) => authStore.hasPermission(p)))
}
