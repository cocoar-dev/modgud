<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter, useRoute, RouterView } from 'vue-router'
import {
    CoarIcon,
    CoarButton,
    CoarContextMenu,
    CoarMenuItem,
    CoarMenuDivider,
    CoarSidebar,
    CoarSidebarItem,
    CoarSidebarDivider,
    CoarSidebarSpacer,
    useContextMenu,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useSignalR } from '@/composables/useSignalR'
import { provideUI } from '@/composables/useUI'
import { useAuthStore } from '@/stores/auth.store'
import LogoutConfirmModal from '@/views/auth/LogoutConfirmModal.vue'

const { t } = useI18n()

const signalR = useSignalR()
const router = useRouter()
const route = useRoute()
const { state: ui, reset: resetUI } = provideUI()
const authStore = useAuthStore()

const collapsed = ref(
    localStorage.getItem('sidebar-collapsed') === 'true'
)

function toggleCollapsed() {
    collapsed.value = !collapsed.value
    localStorage.setItem('sidebar-collapsed', String(collapsed.value))
}

const userInitials = computed(() => {
    const u = authStore.user
    if (u?.Acronym) return u.Acronym.toUpperCase()
    if (u?.Firstname && u?.Lastname) return (u.Firstname[0] + u.Lastname[0]).toUpperCase()
    return u?.UserName?.substring(0, 2).toUpperCase() ?? '??'
})

const userDisplayName = computed(() => {
    const u = authStore.user
    if (u?.Firstname && u?.Lastname) return `${u.Firstname} ${u.Lastname}`
    return u?.UserName ?? ''
})

const userMenu = useContextMenu()

function openUserMenu(event: MouseEvent) {
    const btn = event.currentTarget as HTMLElement
    const rect = btn.getBoundingClientRect()
    userMenu.open({ clientX: rect.right, clientY: rect.bottom + 4 })
}

const showLogoutConfirm = ref(false)

async function logout() {
    // Federated sessions get a choice: local-only vs end IdP session too.
    // Local sessions have no IdP to negotiate with — straight logout.
    if (authStore.user?.IsFederated) {
        showLogoutConfirm.value = true
        return
    }
    await authStore.logout()
}

function goToProfile() {
    router.push('/profile')
}

function openHelp() {
    // End-user docs live at /docs — a separate VitePress app served by the backend.
    // Open in a new tab so the user can keep the app open while reading the docs.
    window.open('/docs/', '_blank', 'noopener')
}

const connectionColor = computed(() =>
    signalR.state.value === 'Connected' ? 'var(--coar-background-semantic-success-bold)' : 'var(--coar-background-semantic-error-bold)'
)

// Permissions that grant access to *any* admin area. Mirrors the resource list
// in `AdminView.vue` so the top-level "Administration" entry hides cleanly when
// the user has zero admin permissions. `hasPermission` already short-circuits
// on `realm:admin` and `<app>:admin`, so we don't need to list those.
const ADMIN_RESOURCE_PERMISSIONS = [
    'cocoar-auth:user:read',
    'cocoar-auth:permission-role:read',
    'cocoar-auth:authorization-group:read',
    'cocoar-auth:oauth-client:read',
    'cocoar-auth:oauth-scope:read',
    'cocoar-auth:oauth-api:read',
    'cocoar-auth:login-provider:read',
    'cocoar-auth:realm:read',
    'cocoar-auth:auth-log:read',
    'cocoar-auth:session:read',
    'cocoar-auth:app:read',
] as const

const hasAnyAdminPermission = computed(() =>
    ADMIN_RESOURCE_PERMISSIONS.some((p) => authStore.hasPermission(p))
)

</script>

<template>
    <div class="flex h-screen flex-col">
        <!-- Header (full width, above sidebar + content) -->
        <header v-if="ui.header.show" class="main-header">
            <!-- Logo area (aligned with sidebar width) -->
            <button @click="router.push('/')" class="header-logo" :style="{ width: collapsed ? '4rem' : '16rem' }">
                <img src="/td-logo-white.svg" alt="Cocoar.Auth" class="header-logo-icon" />
                <span v-if="!collapsed" class="text-sm font-medium tracking-wide opacity-80">Cocoar.Auth</span>
            </button>

            <!-- Header content (90% container) -->
            <div class="header-content">
                <CoarIcon v-if="ui.header.icon" :name="ui.header.icon" class="header-icon" />
                <div class="flex flex-col justify-center" style="line-height: 1.5em">
                    <div class="title" :class="{ 'title-only': !ui.header.subTitle }">
                        {{ ui.header.title }}
                    </div>
                    <div v-if="ui.header.subTitle" class="subtitle">
                        {{ ui.header.subTitle }}
                    </div>
                </div>
                <div class="flex-1"></div>
                <div id="header-outlet-right"></div>

                <!-- User Avatar -->
                <button
                    class="ml-4 flex h-9 w-9 items-center justify-center rounded-full bg-white/20 text-sm font-bold text-white transition hover:bg-white/30"
                    :title="userDisplayName" @click="openUserMenu">
                    {{ userInitials }}
                </button>
            </div>
        </header>

        <!-- Logout confirm (only shown for federated sessions) -->
        <div
            v-if="showLogoutConfirm"
            class="fixed inset-0 z-[1000] flex items-center justify-center bg-black/40"
            @click.self="showLogoutConfirm = false"
        >
            <LogoutConfirmModal :close="() => showLogoutConfirm = false" />
        </div>

        <!-- User Menu -->
        <CoarContextMenu :menu="userMenu">
            <CoarMenuItem :label="userDisplayName" icon="user" @clicked="goToProfile" />
            <CoarMenuDivider />
            <CoarMenuItem :label="t('nav.profile', {}, 'Profile')" icon="circle-user" @clicked="goToProfile" />
            <CoarMenuItem :label="t('nav.logout', {}, 'Logout')" icon="log-out" @clicked="logout" />
        </CoarContextMenu>

        <!-- Body (sidebar + content) -->
        <div class="flex flex-1 overflow-hidden">
            <!-- Sidebar (below header) -->
            <CoarSidebar v-model:collapsed="collapsed" elevated class="z-10">
                <CoarSidebarSpacer height="4px" />
                <CoarSidebarItem icon="layout-dashboard" :label="t('nav.dashboard', {}, 'Dashboard')"
                    :active="route.path === '/dashboard'" @click="router.push('/dashboard')" />
                <template v-if="hasAnyAdminPermission">
                    <CoarSidebarDivider />
                    <CoarSidebarItem icon="cog"
                        :label="t('nav.administration', {}, 'Administration')" :active="route.path.startsWith('/admin')"
                        @click="router.push('/admin')" />
                </template>

                <CoarSidebarSpacer grow />

                <CoarSidebarDivider />
                <CoarSidebarItem icon="circle-help" :label="t('nav.help', {}, 'Help')"
                    @click="openHelp" />

                <template #footer="{ collapsed: c }">
                    <CoarSidebarDivider />
                    <CoarSidebarItem :icon="c ? 'chevron-right' : 'chevron-left'"
                        :label="c ? t('nav.expand', {}, 'Expand') : t('nav.collapse', {}, 'Collapse')"
                        @click="toggleCollapsed" />
                    <!-- SignalR Status -->
                    <div class="h-1 w-full" :style="{ backgroundColor: connectionColor }"></div>
                </template>
            </CoarSidebar>

            <div class="flex flex-1 flex-col overflow-hidden">
                <!-- Content -->
                <main class="flex flex-1 " :style="{ overflow: ui.content.container ? 'auto' : 'hidden' }">
                    <div class="main-container flex" :class="ui.content.container ? 'container-mode' : 'flex-1'">
                        <RouterView />
                    </div>
                </main>

                <!-- Footer (optional, shown when views enable it) -->
                <footer v-if="ui.footer.show" class="main-footer">
                    <div v-if="ui.content.hasSubNav" class="sub-nav-spacer"></div>
                    <div class="flex flex-1 justify-center min-w-0">
                        <div class="flex items-center w-11/12">
                            <div class="flex-1"></div>
                            <div class="flex items-center gap-2">
                                <CoarButton v-if="ui.footer.button3.visible" variant="ghost" size="s"
                                    :disabled="ui.footer.button3.disabled" :loading="ui.footer.button3.loading"
                                    @click="ui.footer.button3.onClick?.()">{{ ui.footer.button3.text }}</CoarButton>
                                <CoarButton v-if="ui.footer.button2.visible" variant="secondary" size="s"
                                    :disabled="ui.footer.button2.disabled" :loading="ui.footer.button2.loading"
                                    @click="ui.footer.button2.onClick?.()">{{ ui.footer.button2.text }}</CoarButton>
                                <CoarButton v-if="ui.footer.button1.visible" variant="primary" size="s"
                                    :disabled="ui.footer.button1.disabled" :loading="ui.footer.button1.loading"
                                    @click="ui.footer.button1.onClick?.()">{{ ui.footer.button1.text }}</CoarButton>
                            </div>
                        </div>
                    </div>
                </footer>
            </div>
        </div>



    </div>
</template>

<style scoped>
.main-header {
    min-height: 64px;
    max-height: 64px;
    background-color: var(--color-header);
    color: white;
    display: flex;
    flex-direction: row;
    box-shadow: 0px 2px 6px #00152959;
    position: relative;
    z-index: 30;
}

.header-logo {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    flex-shrink: 0;
    color: white;
    transition: width 0.2s ease;
}

.header-logo-icon {
    height: 32px;
    width: auto;
}

.header-content {
    display: flex;
    align-items: center;
    flex: 1;
    width: 90%;
    max-width: 100%;
    padding: 0 1.5rem;
}

.header-icon {
    font-size: xx-large;
    width: 52px;
    text-align: left;
}

.title {
    font-size: 1.5em;
    font-weight: bold;
}

.title.title-only {
    font-size: 2em;
}

.subtitle {
    font-size: 0.9em;
}

.main-footer {
    display: flex;
    align-items: center;
    background: var(--coar-background-neutral-secondary, #f7f7f7);
    border-top: 1px solid #e9e9e9;
    padding: 12px 8px;
}

.sub-nav-spacer {
    width: 13rem;
    flex-shrink: 0;
}

.container-mode {
    max-width: 100%;
    width: 90%;
    margin-left: auto;
    margin-right: auto;
}
</style>
