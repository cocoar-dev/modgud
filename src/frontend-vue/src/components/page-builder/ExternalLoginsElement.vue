<script setup lang="ts">
import { CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useLoginPageRuntime } from '@/page-builder/loginPageRuntime'

const { t } = useI18n()
const { externalLogins, startExternalLogin } = useLoginPageRuntime()
</script>

<template>
  <section v-if="externalLogins.length > 0" class="provider-section">
    <div class="provider-divider" aria-hidden="true">
      <span />
      <small>{{ t('common.or', {}, 'or') }}</small>
      <span />
    </div>
    <CoarButton
      v-for="provider in externalLogins"
      :key="provider.Id"
      type="button"
      variant="secondary"
      full-width
      :style="provider.ButtonColorHex
        ? { borderColor: provider.ButtonColorHex, color: provider.ButtonColorHex }
        : undefined"
      @click="startExternalLogin(provider)"
    >
      {{ t('auth.login.externalPrefix', {}, 'Sign in with') }} {{ provider.DisplayName }}
    </CoarButton>
  </section>
</template>

<style scoped>
.provider-section {
  display: grid;
  gap: 0.75rem;
  width: 100%;
}

.provider-divider {
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  gap: 0.75rem;
  color: var(--coar-text-neutral-tertiary);
}

.provider-divider span {
  border-top: 1px solid var(--coar-border-neutral-secondary);
}
</style>
