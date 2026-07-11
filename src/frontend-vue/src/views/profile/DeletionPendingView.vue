<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient } from '@/composables/useHttpClient'
import { isSameOriginPath } from '@/composables/useLoginRedirect'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarButton, CoarIcon } from '@cocoar/vue-ui'
import type { DeletionStatusDto } from '@/models/gdpr'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const gdprHttp = useHttpClient('/api/auth')

const status = ref<DeletionStatusDto | null>(null)
const loading = ref(true)
const cancelling = ref(false)
const error = ref('')

// Same-origin guard on the continue target — never honor an absolute URL.
// Shared with the login flow so '//', '/\' and control-char smuggling are all
// rejected consistently (not just '//').
const continueTarget = computed(() => {
  const r = route.query.redirect
  return isSameOriginPath(r) ? r : '/'
})

const deadlineText = computed(() =>
  status.value?.ConfirmationDeadline
    ? new Date(status.value.ConfirmationDeadline).toLocaleString()
    : '')

onMounted(async () => {
  try {
    status.value = await gdprHttp.addPath('deletion-status').get<DeletionStatusDto>()
  } catch {
    // If we can't read status, don't trap the user — let them proceed.
    proceed()
    return
  }
  // Only self-service grace deletions are cancellable here. Anything else
  // (no pending, or an admin recycle-bin deletion the user can't cancel)
  // should not block the login flow.
  if (!status.value?.IsPending || status.value.Initiator !== 'SelfService') {
    proceed()
    return
  }
  loading.value = false
})

function proceed() {
  const target = continueTarget.value
  if (target.startsWith('/admin') || target === '/') {
    router.push(target)
  } else {
    // External-app redirect target — full navigation.
    window.location.assign(target)
  }
}

async function cancelDeletion() {
  if (cancelling.value) return
  cancelling.value = true
  error.value = ''
  try {
    await gdprHttp.addPath('cancel-deletion').post({})
    proceed()
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
    cancelling.value = false
  }
}
</script>

<template>
  <div class="flex flex-1 items-center justify-center p-4">
    <CoarCard elevated class="w-full max-w-md">
      <div class="p-8 text-center space-y-4">
        <template v-if="loading">
          <h1 class="text-xl font-semibold">{{ t('common.loading', {}, 'Loading…') }}</h1>
        </template>
        <template v-else>
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-amber-100">
            <CoarIcon name="triangle-alert" class="text-amber-600" size="l" />
          </div>
          <h1 class="text-xl font-semibold">
            {{ t('deletionPending.title', {}, 'Your account is scheduled for deletion') }}
          </h1>
          <p class="text-sm text-surface-600">
            {{ t('deletionPending.body', {}, 'Your account will be permanently erased on') }}
            <strong>{{ deadlineText }}</strong>.
            {{ t('deletionPending.bodyHint', {}, 'Cancel now to keep your account, or continue if you still want it deleted.') }}
          </p>

          <p v-if="error" class="text-sm text-red-600">{{ error }}</p>

          <div class="flex flex-col gap-2 pt-2">
            <CoarButton full-width :loading="cancelling" @click="cancelDeletion">
              {{ t('deletionPending.cancel', {}, 'Cancel deletion — keep my account') }}
            </CoarButton>
            <CoarButton full-width variant="ghost" :disabled="cancelling" @click="proceed">
              {{ t('deletionPending.continue', {}, 'Continue anyway') }}
            </CoarButton>
          </div>
        </template>
      </div>
    </CoarCard>
  </div>
</template>
