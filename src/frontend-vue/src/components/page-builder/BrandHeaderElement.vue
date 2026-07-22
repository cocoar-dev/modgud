<script setup lang="ts">
import { useLoginPageRuntime } from '@/page-builder/loginPageRuntime'

const { branding } = useLoginPageRuntime()
</script>

<template>
  <header class="brand-header">
    <img
      :src="branding.LogoUrl ?? '/idp-logo.svg'"
      :alt="branding.ProductName ?? 'Modgud'"
      class="brand-logo"
    />
    <h1 class="brand-name">{{ branding.ProductName ?? 'Modgud' }}</h1>
  </header>
</template>

<style scoped>
.brand-header {
  display: grid;
  justify-items: center;
  gap: 0.35rem;
  text-align: center;
}

.brand-logo {
  display: block;
  width: auto;
  height: 4rem;
  object-fit: contain;
}

.brand-name {
  margin: 0;
  color: var(--coar-text-neutral-primary);
  font-size: 1.5rem;
  font-weight: 700;
  letter-spacing: -0.02em;
}
</style>
