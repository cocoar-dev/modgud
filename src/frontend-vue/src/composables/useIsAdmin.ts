import { computed, type ComputedRef } from 'vue'
import { useAuthStore } from '@/stores/auth.store'

/**
 * Resource permissions that gate any admin-area sidebar item. Mirrors the list
 * in `views/admin/AdminView.vue`. If the user holds any of these (directly or
 * via the `realm:admin` / `<app>:admin` / `<app>:<resource>:admin` bypasses
 * implemented in `authStore.hasPermission`), they get the admin face of the
 * dashboard.
 *
 * Kept in one place so the dashboard and the sidebar can't drift apart: when
 * a new admin resource is added, both pick it up by editing this list.
 */
export const ADMIN_PERMISSIONS: readonly string[] = [
  'cocoar-auth:user:read',
  'cocoar-auth:permission-role:read',
  'cocoar-auth:authorization-group:read',
  'cocoar-auth:login-provider:read',
  'cocoar-auth:oauth-client:read',
  'cocoar-auth:oauth-scope:read',
  'cocoar-auth:oauth-api:read',
  'cocoar-auth:app:read',
  'cocoar-auth:realm:read',
  'cocoar-auth:auth-log:read',
  'cocoar-auth:user:write',
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
