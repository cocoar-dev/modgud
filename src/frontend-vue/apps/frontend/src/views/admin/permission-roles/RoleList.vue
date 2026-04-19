<script setup lang="ts">
import { onMounted } from 'vue'
import { CoarCard } from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'

const ui = useUI()

onMounted(() => {
    ui.set((ctx) => {
        ctx.header.title = 'Permission Roles'
        ctx.header.subTitle = 'Reusable permission bundles for authorization groups'
        ctx.header.icon = 'shield'
    })
})
</script>

<template>
    <div class="list-page">
        <CoarCard>
            <h2>Permission Roles</h2>
            <p class="muted">TODO — permission-role grid will land here in Phase 3.2.</p>
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
