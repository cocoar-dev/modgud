<script setup lang="ts">
import { useRouter, useRoute, RouterView } from 'vue-router'
import { CoarMenu, CoarMenuItem } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

const { t } = useI18n()
const router = useRouter()
const route = useRoute()

interface NavItem {
  label: string
  path: string
  icon?: string
}

const navItems: NavItem[] = [
  { label: '', path: '/admin/users' },
  { label: '', path: '/admin/roles' },
  { label: '', path: '/admin/groups' },
  { label: '', path: '/admin/simulator' },
  { label: '', path: '/admin/auth-log' },
  { label: '', path: '/admin/change-requests' },
  { label: '', path: '/admin/idp-config' },
  { label: '', path: '/admin/settings' },
]

function isActive(item: NavItem): boolean {
  return route.path.startsWith(item.path)
}
</script>

<template>
  <div class="flex min-h-0 flex-1">
    <!-- Left: Navigation menu -->
    <div class="sub-nav flex-shrink-0 p-4 flex flex-col min-h-0">
      <CoarMenu>
        <CoarMenuItem
          icon="users"
          :label="t('nav.users', {}, 'Users')"
          :class="{ 'admin-menu-item--active': isActive(navItems[0]) }"
          @clicked="router.push('/admin/users')"
        />
        <CoarMenuItem
          icon="shield"
          :label="t('nav.roles', {}, 'Roles')"
          :class="{ 'admin-menu-item--active': isActive(navItems[1]) }"
          @clicked="router.push('/admin/roles')"
        />
        <CoarMenuItem
          icon="users"
          :label="t('nav.groups', {}, 'Groups')"
          :class="{ 'admin-menu-item--active': isActive(navItems[2]) }"
          @clicked="router.push('/admin/groups')"
        />
        <CoarMenuItem
          icon="flask-conical"
          :label="t('admin.simulator.title', {}, 'Policy Simulator')"
          :class="{ 'admin-menu-item--active': isActive(navItems[3]) }"
          @clicked="router.push('/admin/simulator')"
        />
        <CoarMenuItem
          icon="scroll-text"
          :label="t('admin.authLog.title', {}, 'Auth Log')"
          :class="{ 'admin-menu-item--active': isActive(navItems[4]) }"
          @clicked="router.push('/admin/auth-log')"
        />
        <CoarMenuItem
          icon="inbox"
          :label="t('admin.changeRequests.title', {}, 'Change requests')"
          :class="{ 'admin-menu-item--active': isActive(navItems[5]) }"
          @clicked="router.push('/admin/change-requests')"
        />
        <CoarMenuItem
          icon="key-round"
          :label="t('admin.idpConfig.title', {}, 'Identity Providers')"
          :class="{ 'admin-menu-item--active': isActive(navItems[6]) }"
          @clicked="router.push('/admin/idp-config')"
        />
        <CoarMenuItem
          icon="settings"
          :label="t('nav.settings', {}, 'Settings')"
          :class="{ 'admin-menu-item--active': isActive(navItems[7]) }"
          @clicked="router.push('/admin/settings')"
        />
      </CoarMenu>
    </div>

    <!-- Right: Content -->
    <div class="flex-1 flex justify-center min-w-0">
      <div class="flex w-11/12">
        <RouterView class="flex-1 min-h-0" />
      </div>
    </div>
  </div>
</template>

<style scoped>
.sub-nav {
  width: 13rem;
  height: 100%;
  --coar-background-neutral-primary: var(--coar-background-neutral-secondary, #f7f7f7);
}

.admin-menu-item--active {
  background: var(--coar-menu-item-background-active, #eff6ff);
  color: var(--coar-menu-item-text-active, #1d4ed8);
  font-weight: 500;
  border-radius: 6px;
}
</style>
