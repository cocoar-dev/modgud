<script setup lang="ts">
import { onMounted } from 'vue'
import { CoarCard } from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'

const ui = useUI()

onMounted(() => {
    ui.set((ctx) => {
        ctx.header.title = 'Authorization Groups'
        ctx.header.subTitle = 'Group members, roles, and access scripts'
        ctx.header.icon = 'users-round'
    })
})
</script>

<template>
    <div class="list-page">
        <CoarCard>
            <h2>Authorization Groups</h2>
            <p class="muted">TODO — authorization-group grid will land here in Phase 3.2.</p>
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
