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
    CoarSidebarHeading,
    CoarSidebarDivider,
    CoarSidebarSpacer,
    useContextMenu,
} from '@cocoar/vue-ui'
import { useSignalR } from '@/composables/useSignalR'
import { provideUI } from '@/composables/useUI'
import { useAuthStore } from '@/stores/auth.store'

const signalR = useSignalR()
const router = useRouter()
const route = useRoute()
const { state: ui } = provideUI()
const authStore = useAuthStore()

const collapsed = ref(localStorage.getItem('sidebar-collapsed') === 'true')

function toggleCollapsed() {
    collapsed.value = !collapsed.value
    localStorage.setItem('sidebar-collapsed', String(collapsed.value))
}

const userInitials = computed(() => {
    const u = authStore.user
    if (u?.FirstName && u?.LastName) return (u.FirstName[0] + u.LastName[0]).toUpperCase()
    return u?.UserName?.substring(0, 2).toUpperCase() ?? '??'
})

const userDisplayName = computed(() => authStore.displayName)

const userMenu = useContextMenu()

function openUserMenu(event: MouseEvent) {
    const btn = event.currentTarget as HTMLElement
    const rect = btn.getBoundingClientRect()
    userMenu.open({ clientX: rect.right, clientY: rect.bottom + 4 })
}

async function logout() {
    await authStore.logout()
    router.push('/login')
}

const connectionColor = computed(() =>
    signalR.state.value === 'Connected'
        ? 'var(--coar-background-semantic-success-bold)'
        : 'var(--coar-background-semantic-error-bold)',
)

// Navigation items (only shown to users with appropriate permissions)
const canSeeAdmin = computed(() => authStore.isAdmin)
const canSeeSystem = computed(() => authStore.hasPermission('system:admin'))
</script>

<template>
    <div class="flex h-screen flex-col">
        <!-- Header (full width, above sidebar + content) -->
        <header v-if="ui.header.show" class="main-header">
            <!-- Logo area (aligned with sidebar width) -->
            <button
                @click="router.push('/')"
                class="header-logo"
                :style="{ width: collapsed ? '4rem' : '16rem' }"
            >
                <span class="text-lg font-bold">CA</span>
                <span v-if="!collapsed" class="text-sm font-medium tracking-wide opacity-80">Cocoar Auth</span>
            </button>

            <!-- Header content -->
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
                    :title="userDisplayName"
                    @click="openUserMenu"
                >
                    {{ userInitials }}
                </button>
            </div>
        </header>

        <!-- User Menu -->
        <CoarContextMenu :menu="userMenu">
            <CoarMenuItem :label="userDisplayName" icon="user" />
            <CoarMenuDivider />
            <CoarMenuItem label="Logout" icon="log-out" @clicked="logout" />
        </CoarContextMenu>

        <!-- Body (sidebar + content) -->
        <div class="flex flex-1 overflow-hidden">
            <!-- Sidebar (below header) -->
            <CoarSidebar v-model:collapsed="collapsed" elevated class="z-10">
                <CoarSidebarSpacer height="4px" />
                <CoarSidebarItem
                    icon="layout-dashboard"
                    label="Dashboard"
                    :active="route.path === '/dashboard' || route.path === '/'"
                    @click="router.push('/dashboard')"
                />

                <template v-if="canSeeAdmin">
                    <CoarSidebarDivider />
                    <CoarSidebarHeading label="Administration" />
                    <CoarSidebarItem
                        icon="users"
                        label="Users"
                        :active="route.path.startsWith('/admin/users')"
                        @click="router.push('/admin/users')"
                    />
                    <CoarSidebarItem
                        icon="shield-check"
                        label="Roles"
                        :active="route.path.startsWith('/admin/roles')"
                        @click="router.push('/admin/roles')"
                    />
                    <CoarSidebarItem
                        icon="key-round"
                        label="OAuth Clients"
                        :active="route.path.startsWith('/admin/oauth/clients')"
                        @click="router.push('/admin/oauth/clients')"
                    />
                    <CoarSidebarItem
                        icon="scan-line"
                        label="OAuth Scopes"
                        :active="route.path.startsWith('/admin/oauth/scopes')"
                        @click="router.push('/admin/oauth/scopes')"
                    />
                    <CoarSidebarItem
                        icon="server"
                        label="OAuth APIs"
                        :active="route.path.startsWith('/admin/oauth/apis')"
                        @click="router.push('/admin/oauth/apis')"
                    />
                    <CoarSidebarItem
                        icon="lock"
                        label="Login Providers"
                        :active="route.path.startsWith('/admin/login-providers')"
                        @click="router.push('/admin/login-providers')"
                    />

                    <CoarSidebarDivider />
                    <CoarSidebarHeading label="Authorization" />
                    <CoarSidebarItem
                        icon="users-round"
                        label="Authorization Groups"
                        :active="route.path.startsWith('/admin/authorization-groups')"
                        @click="router.push('/admin/authorization-groups')"
                    />
                    <CoarSidebarItem
                        icon="shield"
                        label="Permission Roles"
                        :active="route.path.startsWith('/admin/permission-roles')"
                        @click="router.push('/admin/permission-roles')"
                    />
                </template>

                <template v-if="canSeeSystem">
                    <CoarSidebarDivider />
                    <CoarSidebarHeading label="System" />
                    <CoarSidebarItem
                        icon="globe"
                        label="Realms"
                        :active="route.path.startsWith('/admin/realms')"
                        @click="router.push('/admin/realms')"
                    />
                </template>

                <CoarSidebarSpacer grow />

                <template #footer="{ collapsed: c }">
                    <CoarSidebarDivider />
                    <CoarSidebarItem
                        :icon="c ? 'chevron-right' : 'chevron-left'"
                        :label="c ? 'Expand' : 'Collapse'"
                        @click="toggleCollapsed"
                    />
                    <!-- SignalR Status -->
                    <div class="h-1 w-full" :style="{ backgroundColor: connectionColor }"></div>
                </template>
            </CoarSidebar>

            <div class="flex flex-1 flex-col overflow-hidden">
                <!-- Content -->
                <main class="flex flex-1" :style="{ overflow: ui.content.container ? 'auto' : 'hidden' }">
                    <div
                        class="main-container flex"
                        :class="ui.content.container ? 'container-mode' : 'flex-1'"
                    >
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
                                <CoarButton
                                    v-if="ui.footer.button3.visible"
                                    variant="ghost"
                                    size="s"
                                    :disabled="ui.footer.button3.disabled"
                                    :loading="ui.footer.button3.loading"
                                    @click="ui.footer.button3.onClick?.()"
                                >
                                    {{ ui.footer.button3.text }}
                                </CoarButton>
                                <CoarButton
                                    v-if="ui.footer.button2.visible"
                                    variant="danger"
                                    size="s"
                                    :disabled="ui.footer.button2.disabled"
                                    :loading="ui.footer.button2.loading"
                                    @click="ui.footer.button2.onClick?.()"
                                >
                                    {{ ui.footer.button2.text }}
                                </CoarButton>
                                <CoarButton
                                    v-if="ui.footer.button1.visible"
                                    variant="primary"
                                    size="s"
                                    :disabled="ui.footer.button1.disabled"
                                    :loading="ui.footer.button1.loading"
                                    @click="ui.footer.button1.onClick?.()"
                                >
                                    {{ ui.footer.button1.text }}
                                </CoarButton>
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
    background-color: var(--coar-background-accent-primary, #1f2937);
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
    border-top: 1px solid var(--coar-border-neutral-secondary, #e9e9e9);
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
