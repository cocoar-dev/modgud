<script setup lang="ts">
import { onMounted } from 'vue'
import { CoarCard } from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'

const ui = useUI()

onMounted(() => {
    ui.set((ctx) => {
        ctx.header.title = 'Users'
        ctx.header.subTitle = 'Manage user accounts'
        ctx.header.icon = 'users'
    })
})
</script>

<template>
    <div class="list-page">
        <CoarCard>
            <h2>Users</h2>
            <p class="muted">TODO — user grid will land here in Phase 3.2.</p>
        </CoarCard>
    </div>
</template>

<style scoped>
.list-page {
    padding: 1.5rem 0;
    width: 100%;
}
.muted {
    color: var(--coar-text-neutral-secondary);
}
h2 {
    margin: 0 0 0.5rem 0;
    font-size: 1.25rem;
    font-weight: 700;
}
</style>
