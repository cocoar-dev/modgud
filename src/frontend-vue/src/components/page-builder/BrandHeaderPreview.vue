<template>
  <div class="brand-preview" aria-hidden="true">
    <div class="logo-placeholder">M</div>
    <strong>Application branding</strong>
  </div>
</template>

<style scoped>
.brand-preview {
  display: grid;
  justify-items: center;
  gap: 0.35rem;
  padding: 0.5rem;
  color: var(--coar-text-neutral-secondary);
}

.logo-placeholder {
  display: grid;
  width: 3rem;
  height: 3rem;
  place-items: center;
  border-radius: var(--coar-radius-l);
  background: var(--coar-background-accent-secondary);
  color: var(--coar-text-accent-primary);
  font-weight: 700;
}
</style>
