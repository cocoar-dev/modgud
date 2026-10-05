<script setup lang="ts">
import { CoarTag } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

// ADR 0025 §9 — marks an App setting that only (or partly) takes effect when a request
// arrives through the App's own domain, because its readers resolve the App from the Host
// instead of from the client or the target. `ineffective` turns it into a warning for an
// App that has no own domain: the setting is configured but currently does nothing.
defineProps<{
  kind: 'host' | 'partial'
  ineffective?: boolean
}>()

const { t } = useI18n()
</script>

<template>
  <CoarTag :variant="ineffective ? 'warning' : 'neutral'" size="s">
    <template v-if="kind === 'host'">{{ t('admin.appSettings.hostBound.host', {}, `Only via the app's own domain`) }}</template>
    <template v-else>{{ t('admin.appSettings.hostBound.partial', {}, `Partly via the app's own domain`) }}</template>
  </CoarTag>
</template>
