<script setup lang="ts">
import { CoarIcon, CoarAvatar, CoarCard } from '@cocoar/vue-ui';
import { useAuthStore } from '@/stores/auth.store';
import { useUI } from '@/composables/useUI';

const auth = useAuthStore();
const ui = useUI();

ui.set(ctx => {
  ctx.header.show = false;
  ctx.content.scrollable = true;
});
</script>

<template>
  <div class="page">
    <CoarCard padding="l" class="hero">
      <div class="hero-inner">
        <CoarAvatar :name="auth.displayName || auth.currentUser?.userName || '?'" size="l" />
        <div class="hero-text">
          <h1 class="hero-title">Welcome back, {{ auth.displayName || auth.currentUser?.userName }}</h1>
          <p class="hero-subtitle">Manage your account, security settings, and active sessions.</p>
        </div>
      </div>
    </CoarCard>

    <div class="section-label">Quick access</div>

    <div class="cards-grid">
      <RouterLink to="/profile" class="card-link">
        <CoarCard padding="m" class="nav-card">
          <div class="nav-card-inner">
            <div class="nav-card-icon-wrap nav-card-icon-wrap--blue">
              <CoarIcon name="user" size="s" />
            </div>
            <div class="nav-card-body">
              <div class="nav-card-title">Profile</div>
              <div class="nav-card-desc">Personal info &amp; 2FA settings</div>
            </div>
          </div>
        </CoarCard>
      </RouterLink>

      <RouterLink to="/sessions" class="card-link">
        <CoarCard padding="m" class="nav-card">
          <div class="nav-card-inner">
            <div class="nav-card-icon-wrap nav-card-icon-wrap--green">
              <CoarIcon name="monitor" size="s" />
            </div>
            <div class="nav-card-body">
              <div class="nav-card-title">Sessions</div>
              <div class="nav-card-desc">View and revoke active logins</div>
            </div>
          </div>
        </CoarCard>
      </RouterLink>

      <RouterLink to="/privacy" class="card-link">
        <CoarCard padding="m" class="nav-card">
          <div class="nav-card-inner">
            <div class="nav-card-icon-wrap nav-card-icon-wrap--purple">
              <CoarIcon name="shield" size="s" />
            </div>
            <div class="nav-card-body">
              <div class="nav-card-title">Privacy &amp; Data</div>
              <div class="nav-card-desc">Export or delete your account data</div>
            </div>
          </div>
        </CoarCard>
      </RouterLink>

      <RouterLink v-if="auth.isAdmin" to="/admin" class="card-link">
        <CoarCard padding="m" class="nav-card">
          <div class="nav-card-inner">
            <div class="nav-card-icon-wrap nav-card-icon-wrap--amber">
              <CoarIcon name="settings" size="s" />
            </div>
            <div class="nav-card-body">
              <div class="nav-card-title">Admin Panel</div>
              <div class="nav-card-desc">Users, roles, and OAuth clients</div>
            </div>
          </div>
        </CoarCard>
      </RouterLink>
    </div>
  </div>
</template>

<style scoped>
/* Hero */
.hero { margin-bottom: 2.5rem; }
.hero-inner { display: flex; align-items: center; gap: 1.25rem; }
.hero-text { flex: 1; min-width: 0; }
.hero-title { margin: 0 0 0.25rem; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); letter-spacing: -0.02em; }
.hero-subtitle { margin: 0; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }

/* Section label */
.section-label { font-size: 0.75rem; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--coar-text-neutral-tertiary, #94a3b8); margin-bottom: 0.75rem; }

/* Grid */
.cards-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 0.875rem; }

/* Card links */
.card-link { text-decoration: none; color: inherit; }

/* Nav cards */
.nav-card-inner { display: flex; align-items: center; gap: 1rem; }

.nav-card-icon-wrap {
  flex-shrink: 0;
  width: 2.5rem;
  height: 2.5rem;
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
}
.nav-card-icon-wrap--blue   { background: var(--coar-background-semantic-info-subtle); color: var(--coar-text-accent-primary); }
.nav-card-icon-wrap--green  { background: var(--coar-background-semantic-success-subtle); color: var(--coar-background-semantic-success-bold); }
.nav-card-icon-wrap--purple { background: var(--coar-background-accent-tertiary); color: var(--coar-text-accent-secondary); }
.nav-card-icon-wrap--amber  { background: var(--coar-background-semantic-warning-subtle); color: var(--coar-background-semantic-warning-bold); }

.nav-card-body { flex: 1; min-width: 0; }
.nav-card-title { font-weight: 600; font-size: 0.9375rem; color: var(--coar-text-neutral-primary); margin-bottom: 0.2rem; }
.nav-card-desc { font-size: 0.8125rem; color: var(--coar-text-neutral-secondary); }
</style>
