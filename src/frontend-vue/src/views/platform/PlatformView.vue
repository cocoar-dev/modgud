<script setup lang="ts">
import { computed } from 'vue'
import { RouterView } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import SubNavLayoutGrouped from '@/layouts/SubNavLayoutGrouped.vue'
import type { SubNavGroup, SubNavItem } from '@/layouts/sub-nav-types'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'

/**
 * Platform area — operator-facing config for the IdP itself (vs. AdminView
 * which is tenant/realm-admin work). Two thematic groups:
 *   - Anpassung: visual + content (branding, custom pages, asset library)
 *   - Betrieb: runtime + ops (observability, inbox retention, app settings)
 *
 * Permission-Gating sits on the items (`visible`), the wrapper itself is
 * gated by MainLayout's `hasAnyPlatformPermission` so an unprivileged user
 * never even sees the sidebar entry.
 */
const { t } = useI18n()
const authStore = useAuthStore()
const appConfig = useAppConfigStore()

const groups = computed<SubNavGroup[]>(() => [
  {
    title: t('admin.section.customization', {}, 'Customization'),
    items: [
      {
        label: t('admin.customization.branding.title', {}, 'Branding'),
        icon: 'palette',
        to: '/platform/customization/branding',
        visible: authStore.hasPermission('realm-settings:read'),
      } satisfies SubNavItem,
      {
        label: t('admin.customization.pages.title', {}, 'Pages'),
        icon: 'layout-template',
        to: '/platform/customization/pages',
        visible: authStore.hasPermission('realm-settings:read') && !!appConfig.config.Features.PageBuilder,
      } satisfies SubNavItem,
      {
        label: t('admin.assets.title', {}, 'Asset Library'),
        icon: 'image',
        to: '/platform/customization/assets',
        visible: authStore.hasPermission('asset:read'),
      } satisfies SubNavItem,
    ],
  },
  {
    title: t('platform.section.operations', {}, 'Operations'),
    items: [
      {
        label: t('admin.observability.title', {}, 'Observability'),
        icon: 'activity',
        to: '/platform/observability',
        visible: authStore.hasPermission('observability:read'),
      } satisfies SubNavItem,
      {
        label: t('admin.inboxSettings.title', {}, 'Inbox Settings'),
        icon: 'inbox',
        to: '/platform/inbox-settings',
        visible: authStore.hasPermission('inbox-settings:read'),
      } satisfies SubNavItem,
      {
        label: t('nav.settings', {}, 'Settings'),
        icon: 'settings',
        to: '/platform/settings',
        visible: authStore.hasPermission('realm:admin'),
      } satisfies SubNavItem,
    ],
  },
])
</script>

<template>
  <SubNavLayoutGrouped :groups="groups">
    <RouterView class="flex-1 min-h-0" />
  </SubNavLayoutGrouped>
</template>
