<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { RouterView } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import SubNavLayoutGrouped from '@/layouts/SubNavLayoutGrouped.vue'
import type { SubNavGroup } from '@/layouts/sub-nav-types'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useRealmDraftStore } from '@/stores/realmDraft.store'
import { adminNavigation, canSeeAdminItem } from '@/router/admin-navigation'

const { t } = useI18n()
const authStore = useAuthStore()
const appConfig = useAppConfigStore()
const draftStore = useRealmDraftStore()

// Parked and shared drafts are easy to forget once the staging bar only shows
// the active one — the sidebar entry carries their count.
onMounted(() => {
  if (authStore.hasPermission('realm:admin')) void draftStore.loadDrafts()
})
const otherDraftCount = computed(() =>
  draftStore.drafts.filter((d) => d.Id !== draftStore.current?.Id).length)

const adminGroups = computed<SubNavGroup[]>(() =>
  adminNavigation.map((section) => ({
    title: t(section.heading, {}, section.headingEn),
    items: section.items.map((item) => ({
      label: t(item.label, {}, item.labelEn),
      icon: item.icon,
      to: item.to,
      visible: canSeeAdminItem(item, authStore.hasPermission, appConfig.config.Features),
      ...(item.to === '/admin/realm-config' ? {
        badge: otherDraftCount.value,
        badgeTitle: t('admin.realmConfig.otherDrafts', { count: otherDraftCount.value }, `${otherDraftCount.value} parked or shared draft(s) besides the active one`),
      } : {}),
    })),
  })),
)
</script>

<template>
  <SubNavLayoutGrouped :groups="adminGroups">
    <!-- The ADR-0017 staging bar renders footer-positioned in MainLayout. -->
    <RouterView class="flex-1 min-h-0 min-w-0" />
  </SubNavLayoutGrouped>
</template>
