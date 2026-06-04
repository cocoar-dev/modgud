<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useAuthStore } from '@/stores/auth.store'
import AuditLogView from './AuditLogView.vue'
import AuthLogView from './AuthLogView.vue'

// Combined "Logs" home for the two tenant-admin log surfaces (logging/audit
// redesign): the GDPR audit trail (audit-log:read, /api/admin/audit) and the
// streamless security/ops store (auth-log:read, /api/admin/auth-log). They were
// two separate sidebar items; this wraps both under tabs. Each tab is gated by
// its own permission, so a user with only one of the two sees only that tab.
// (The platform operational error feed stays separate under Observability —
// different audience + observability:read.)
const { t, language } = useI18n()
const ui = useUI()
const route = useRoute()
const router = useRouter()
const authStore = useAuthStore()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.logs.title', {}, 'Logs')
  ctx.header.icon = 'scroll-text'
  ctx.content.container = false
}), { immediate: true })

type TabId = 'audit' | 'security'

const canAudit = computed(() => authStore.hasPermission('audit-log:read'))
const canSecurity = computed(() => authStore.hasPermission('auth-log:read'))

// Resolve the requested tab if the caller may see it, else fall back to the
// first tab they can (audit preferred). Guards against deep-linking a tab the
// user lacks the permission for.
function resolveTab(requested: unknown): TabId {
  if (requested === 'audit' && canAudit.value) return 'audit'
  if (requested === 'security' && canSecurity.value) return 'security'
  return canAudit.value ? 'audit' : 'security'
}

const activeTab = ref<TabId>(resolveTab(route.query.tab))

// Keep the URL query in sync so a tab is bookmarkable and the old-route
// redirects (/admin/audit, /admin/auth-log) land on the right tab.
watch(activeTab, (tab) => {
  if (route.query.tab !== tab) router.replace({ query: { ...route.query, tab } })
})
watch(() => route.query.tab, (q) => {
  const resolved = resolveTab(q)
  if (resolved !== activeTab.value) activeTab.value = resolved
})
</script>

<template>
  <div class="flex flex-1 flex-col min-h-0">
    <CoarTabGroup v-model="activeTab" class="logs-tab-bar">
      <CoarTab v-if="canAudit" id="audit">
        {{ t('admin.logs.tabs.audit', {}, 'Audit') }}
      </CoarTab>
      <CoarTab v-if="canSecurity" id="security">
        {{ t('admin.logs.tabs.security', {}, 'Security') }}
      </CoarTab>
    </CoarTabGroup>

    <!-- Lazy-mounted: only the active tab's grid runs (and polls). -->
    <AuditLogView v-if="activeTab === 'audit' && canAudit" />
    <AuthLogView v-else-if="activeTab === 'security' && canSecurity" />
  </div>
</template>

<style scoped>
.logs-tab-bar {
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  padding: 0 1rem;
}
</style>
