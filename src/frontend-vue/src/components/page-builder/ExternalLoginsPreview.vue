<script setup lang="ts">
import { CoarButton } from '@cocoar/vue-ui'
</script>

<template>
  <div class="provider-preview" aria-hidden="true">
    <div class="provider-divider"><span /><small>or</small><span /></div>
    <CoarButton variant="secondary" full-width disabled>Sign in with Provider</CoarButton>
  </div>
</template>

<style scoped>
.provider-preview {
  display: grid;
  gap: 0.5rem;
  width: 100%;
}

.provider-divider {
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  gap: 0.5rem;
  color: var(--coar-text-neutral-tertiary);
}

.provider-divider span { border-top: 1px solid var(--coar-border-neutral-secondary); }
</style>
