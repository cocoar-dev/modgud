<script setup lang="ts">
import { onMounted, computed } from 'vue'
import { CoarCard } from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'
import { useAuthStore } from '@/stores/auth.store'

const ui = useUI()
const authStore = useAuthStore()

const displayName = computed(() => authStore.displayName || authStore.user?.UserName || 'friend')
const permissionCount = computed(() => authStore.permissions.length)
const roleCount = computed(() => authStore.roles.length)
const realm = computed(() => authStore.user?.Realm ?? '—')

onMounted(() => {
    ui.set((ctx) => {
        ctx.header.title = 'Dashboard'
        ctx.header.subTitle = 'Overview of your account'
        ctx.header.icon = 'layout-dashboard'
    })
})
</script>

<template>
    <div class="dashboard">
        <CoarCard>
            <h2 class="welcome">Welcome, {{ displayName }}</h2>
            <p class="muted">Signed in to realm <strong>{{ realm }}</strong>.</p>
        </CoarCard>

        <div class="stats">
            <CoarCard>
                <div class="stat-label">Roles</div>
                <div class="stat-value">{{ roleCount }}</div>
            </CoarCard>
            <CoarCard>
                <div class="stat-label">Permissions</div>
                <div class="stat-value">{{ permissionCount }}</div>
            </CoarCard>
        </div>

        <CoarCard>
            <h3 class="section-title">Your permissions</h3>
            <div v-if="permissionCount === 0" class="muted">No permissions assigned.</div>
            <ul v-else class="perm-list">
                <li v-for="perm in authStore.permissions" :key="perm">
                    <code>{{ perm }}</code>
                </li>
            </ul>
        </CoarCard>
    </div>
</template>

<style scoped>
.dashboard {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    padding: 1.5rem 0;
    width: 100%;
}

.welcome {
    margin: 0 0 0.5rem 0;
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--coar-text-neutral-primary);
    letter-spacing: -0.02em;
}

.muted {
    color: var(--coar-text-neutral-secondary);
    margin: 0;
}

.stats {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: 1rem;
}

.stat-label {
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--coar-text-neutral-secondary);
}

.stat-value {
    font-size: 2rem;
    font-weight: 700;
    color: var(--coar-text-neutral-primary);
    margin-top: 0.25rem;
}

.section-title {
    margin: 0 0 0.75rem 0;
    font-size: 1rem;
    font-weight: 600;
}

.perm-list {
    margin: 0;
    padding: 0;
    list-style: none;
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
}

.perm-list code {
    font-family: var(--coar-font-mono, monospace);
    font-size: 0.8125rem;
    color: var(--coar-text-neutral-primary);
    background: var(--coar-background-neutral-tertiary, #f1f5f9);
    padding: 0.125rem 0.375rem;
    border-radius: 4px;
}
</style>
