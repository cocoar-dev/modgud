<script setup lang="ts">
import { useRoute, useRouter, RouterView } from 'vue-router';
import { CoarSidebar, CoarMenu, CoarMenuItem, CoarMenuHeading, CoarAvatar, CoarButton } from '@cocoar/vue-ui';
import { useAuthStore } from '@/stores/auth.store';
import { useDarkMode } from '@/composables/useDarkMode';
import { useUI } from '@/composables/useUI';

const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const { isDark, toggle: toggleDark } = useDarkMode();
const ui = useUI();

function onLogout() {
  auth.logout('/login');
}
</script>

<template>
  <div class="app-layout">
    <CoarSidebar class="app-sidebar">
      <template #header>
        <div class="sidebar-header">
          <div class="sidebar-brand">
            <span class="sidebar-brand-icon">⚡</span>
            <h1 class="sidebar-logo">Cocoar Auth</h1>
          </div>
          <div class="sidebar-realm">
            <span class="sidebar-realm-label">Realm</span>
            <span class="sidebar-realm-name">{{ auth.currentUser?.realm ?? '—' }}</span>
          </div>
          <div class="sidebar-user">
            <CoarAvatar :name="auth.displayName || auth.currentUser?.userName || '?'" size="s" />
            <span class="sidebar-user-name">{{ auth.displayName || auth.currentUser?.userName }}</span>
          </div>
        </div>
      </template>

      <CoarMenu borderless>
        <CoarMenuHeading>Account</CoarMenuHeading>
        <CoarMenuItem
          label="Home"
          icon="home"
          :class="{ 'nav-item--active': route.path === '/' }"
          @clicked="router.push('/')"
        />
        <CoarMenuItem
          label="Profile"
          icon="user"
          :class="{ 'nav-item--active': route.path === '/profile' }"
          @clicked="router.push('/profile')"
        />
        <CoarMenuItem
          label="Sessions"
          icon="monitor"
          :class="{ 'nav-item--active': route.path === '/sessions' }"
          @clicked="router.push('/sessions')"
        />
        <CoarMenuItem
          label="Privacy"
          icon="shield"
          :class="{ 'nav-item--active': route.path === '/privacy' }"
          @clicked="router.push('/privacy')"
        />

        <template v-if="auth.isAdmin">
          <CoarMenuHeading>System</CoarMenuHeading>
          <CoarMenuItem
            label="Realms"
            icon="globe"
            :class="{ 'nav-item--active': route.path.startsWith('/admin/realms') }"
            @clicked="router.push('/admin/realms')"
          />
        </template>

        <template v-if="auth.isAdmin">
          <CoarMenuHeading>Administration</CoarMenuHeading>
          <CoarMenuItem
            label="Users"
            icon="users"
            :class="{ 'nav-item--active': route.path.startsWith('/admin/users') }"
            @clicked="router.push('/admin/users')"
          />
          <CoarMenuItem
            label="Roles"
            icon="shield-check"
            :class="{ 'nav-item--active': route.path.startsWith('/admin/roles') }"
            @clicked="router.push('/admin/roles')"
          />
          <CoarMenuItem
            label="Clients"
            icon="key-round"
            :class="{ 'nav-item--active': route.path.startsWith('/admin/oauth/clients') }"
            @clicked="router.push('/admin/oauth/clients')"
          />
          <CoarMenuItem
            label="Scopes"
            icon="scan-line"
            :class="{ 'nav-item--active': route.path.startsWith('/admin/oauth/scopes') }"
            @clicked="router.push('/admin/oauth/scopes')"
          />
          <CoarMenuItem
            label="APIs"
            icon="server"
            :class="{ 'nav-item--active': route.path.startsWith('/admin/oauth/apis') }"
            @clicked="router.push('/admin/oauth/apis')"
          />
          <CoarMenuItem
            label="Login Providers"
            icon="lock"
            :class="{ 'nav-item--active': route.path.startsWith('/admin/login-providers') }"
            @clicked="router.push('/admin/login-providers')"
          />
        </template>
      </CoarMenu>

      <template #footer>
        <div class="sidebar-footer">
          <CoarMenu borderless>
            <CoarMenuItem
              :label="isDark ? 'Light Mode' : 'Dark Mode'"
              :icon="isDark ? 'sun' : 'moon'"
              @clicked="toggleDark"
            />
            <CoarMenuItem
              label="Sign Out"
              icon="log-out"
              @clicked="onLogout"
            />
          </CoarMenu>
        </div>
      </template>
    </CoarSidebar>

    <div class="main-wrapper">
      <header v-if="ui.state.header.show && ui.state.header.title" class="page-header">
        <div class="page-container">
          <div class="page-header-titles">
            <h1 class="page-title">{{ ui.state.header.title }}</h1>
            <p v-if="ui.state.header.subTitle" class="page-subtitle">{{ ui.state.header.subTitle }}</p>
          </div>
        </div>
      </header>
      <div v-if="ui.state.content.showLoadingBar" class="loading-bar">
        <div class="loading-bar-progress" />
      </div>
      <main class="main-content" :class="{ 'main-content--scrollable': ui.state.content.scrollable, 'main-content--padded': ui.state.content.padding }">
        <div v-if="ui.state.content.container" class="page-container page-container--content">
          <RouterView />
        </div>
        <div v-else class="page-fullwidth">
          <RouterView />
        </div>
      </main>
      <footer v-if="ui.state.footer.show" class="page-footer">
        <div class="page-container page-footer-inner">
          <CoarButton
            v-if="ui.state.footer.button1.visible"
            variant="secondary"
            :disabled="ui.state.footer.button1.disabled"
            :loading="ui.state.footer.button1.loading"
            @click="ui.state.footer.button1.onClick?.()"
          >
            {{ ui.state.footer.button1.text }}
          </CoarButton>
          <div class="footer-spacer" />
          <CoarButton
            v-if="ui.state.footer.button2.visible"
            variant="danger"
            :disabled="ui.state.footer.button2.disabled"
            :loading="ui.state.footer.button2.loading"
            @click="ui.state.footer.button2.onClick?.()"
          >
            {{ ui.state.footer.button2.text }}
          </CoarButton>
          <CoarButton
            v-if="ui.state.footer.button3.visible"
            variant="primary"
            :disabled="ui.state.footer.button3.disabled"
            :loading="ui.state.footer.button3.loading"
            @click="ui.state.footer.button3.onClick?.()"
          >
            {{ ui.state.footer.button3.text }}
          </CoarButton>
        </div>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.app-layout {
  display: flex;
  min-height: 100vh;
  max-height: 100vh;
  overflow: hidden;
}

.app-sidebar {
  width: 260px;
  min-width: 260px;
  height: 100vh;
  --coar-sidebar-background: var(--coar-background-neutral-secondary);
  --coar-sidebar-border: none;
  box-shadow: var(--coar-shadow-right);
  position: relative;
  z-index: 10;
}

.sidebar-header {
  padding: 1.25rem 1.25rem 1rem;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.sidebar-brand {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.sidebar-brand-icon {
  font-size: 1.25rem;
  line-height: 1;
}

.sidebar-logo {
  margin: 0;
  font-size: 0.9375rem;
  font-weight: 700;
  color: var(--coar-text-neutral-primary);
  letter-spacing: -0.01em;
}

.sidebar-realm {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.375rem 0.5rem;
  background: var(--coar-background-accent-tertiary, #eff6ff);
  border-radius: 6px;
  margin-top: 0.25rem;
}

.sidebar-realm-label {
  font-size: 0.6875rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary);
}

.sidebar-realm-name {
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--coar-text-accent-primary);
}

.sidebar-user {
  display: flex;
  align-items: center;
  gap: 0.625rem;
  padding: 0.5rem 0.25rem;
  border-top: 1px solid var(--coar-border-neutral-tertiary, #e2e8f0);
  padding-top: 0.875rem;
}

.sidebar-user-name {
  font-size: 0.8125rem;
  font-weight: 500;
  color: var(--coar-text-neutral-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.sidebar-footer {
  padding: 0.375rem 0.75rem 0.75rem;
}

.main-wrapper {
  flex: 1;
  background: var(--coar-background-neutral-primary);
  height: 100vh;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

/* Shared centered container */
.page-container {
  width: 85%;
  max-width: 1200px;
  margin: 0 auto;
}

.page-container--content {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.page-fullwidth {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding: 0 1.5rem;
}

/* Header - fixed 64px height */
.page-header {
  height: 64px;
  display: flex;
  align-items: center;
  border-bottom: 1px solid var(--coar-border-neutral-tertiary, #e2e8f0);
  flex-shrink: 0;
}

.page-header-titles {
  min-width: 0;
}

.page-title {
  margin: 0;
  font-size: 1.25rem;
  font-weight: 700;
  color: var(--coar-text-neutral-primary);
  letter-spacing: -0.02em;
  line-height: 1.2;
}

.page-subtitle {
  margin: 0.125rem 0 0;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary);
  line-height: 1.2;
}

.loading-bar {
  height: 3px;
  background: var(--coar-background-neutral-secondary);
  overflow: hidden;
  flex-shrink: 0;
}

.loading-bar-progress {
  height: 100%;
  width: 30%;
  background: var(--coar-text-accent-primary);
  animation: loading-slide 1.5s ease-in-out infinite;
}

@keyframes loading-slide {
  0% { transform: translateX(-100%); }
  100% { transform: translateX(400%); }
}

.main-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
}

.main-content--scrollable {
  overflow-y: auto;
}

.main-content--padded {
  padding-top: 1.5rem;
  padding-bottom: 1.5rem;
}

/* Footer - fixed 64px height */
.page-footer {
  height: 64px;
  display: flex;
  align-items: center;
  border-top: 1px solid var(--coar-border-neutral-tertiary, #e2e8f0);
  flex-shrink: 0;
}

.page-footer-inner {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.footer-spacer {
  flex: 1;
}
</style>

<style>
/* Active nav item — same pattern as Showcase */
.nav-item--active {
  background: var(--coar-background-accent-tertiary) !important;
  color: var(--coar-text-accent-primary) !important;
}

.nav-item--active .coar-menu-item__label {
  color: var(--coar-text-accent-primary) !important;
  font-weight: var(--coar-body-base-bold-weight) !important;
}
</style>
