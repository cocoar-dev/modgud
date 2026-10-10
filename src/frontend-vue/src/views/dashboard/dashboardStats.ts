import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'

// Mirrors DashboardStatsDto in Modgud.Api/Features/Dashboard/DashboardEndpoints.cs.
// Every block the viewer has no permission for arrives as null.

export interface DashboardCounts {
  Users: number | null
  ServiceAccounts: number | null
  Groups: number | null
  Roles: number | null
  Apps: number | null
  OAuthClients: number | null
  LoginProviders: number | null
  LoginProvidersEnabled: number | null
  ActiveSessions: number | null
  PendingChangeRequests: number | null
}

export interface LoginStats {
  Days: number
  Succeeded: number
  Failed: number
  FailedLast24h: number
  Series: { Day: string; Succeeded: number; Failed: number }[]
  Methods: { Method: string; Count: number }[]
}

export interface SecurityStats {
  Days: number
  Info: number
  Warning: number
  Error: number
  AttentionLast24h: number
  Series: { Day: string; Info: number; Warning: number; Error: number }[]
  TopEventTypes: { EventType: string; Count: number }[]
}

export interface DashboardStats {
  Days: number
  TimeZone: string
  Counts: DashboardCounts
  Logins: LoginStats | null
  Security: SecurityStats | null
}

// One read backs every statistics widget, so the state is shared rather than
// fetched per widget.
const stats = ref<DashboardStats | null>(null)
const loading = ref(false)
const failed = ref(false)

async function load() {
  loading.value = true
  failed.value = false
  try {
    // The browser's zone, so "today" on the chart is the viewer's today.
    const tz = Intl.DateTimeFormat().resolvedOptions().timeZone
    stats.value = await useHttpClient('/api/dashboard/stats')
      .setQueryParameter('tz', tz)
      .get<DashboardStats>()
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
}

export function useDashboardStats() {
  return { stats, loading, failed, load }
}
