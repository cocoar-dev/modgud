<script setup lang="ts">
</script>

<template>
  <div class="not-found-page">
    <div class="not-found-card">
      <p class="not-found-code">404</p>
      <h1 class="not-found-title">Page not found</h1>
      <p class="not-found-desc">The page you're looking for doesn't exist or has been moved.</p>
      <RouterLink to="/" class="home-link">Go to Home</RouterLink>
    </div>
  </div>
</template>

<style scoped>
.not-found-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
}

.not-found-card {
  text-align: center;
  max-width: 400px;
}

.not-found-code {
  margin: 0 0 0.5rem;
  font-size: 6rem;
  font-weight: 700;
  color: var(--coar-text-accent-primary);
  line-height: 1;
}

.not-found-title {
  margin: 0 0 0.75rem;
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--coar-text-neutral-primary);
}

.not-found-desc {
  margin: 0 0 2rem;
  color: var(--coar-text-neutral-secondary);
  font-size: 0.9375rem;
}

.home-link {
  display: inline-block;
  padding: 0.625rem 1.5rem;
  background: var(--coar-background-accent-primary, #0066cc);
  color: #fff;
  border-radius: var(--coar-radius-md, 6px);
  text-decoration: none;
  font-weight: 500;
  font-size: 0.9375rem;
}

.home-link:hover {
  opacity: 0.9;
}
</style>
