<script setup lang="ts">
import { ref, watch } from 'vue'
import { CoarScriptEditor } from '@cocoar/vue-script-editor'
import { CoarButton, useDialog } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import UserUpdateScriptTestDialog from './UserUpdateScriptTestDialog.vue'

const { t } = useI18n()
const dialog = useDialog()

const props = defineProps<{
  modelValue: string
  loginProviderId?: string
  isNew?: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: string): void
}>()

const script = ref(props.modelValue)
watch(() => props.modelValue, (v) => { script.value = v })
watch(script, (v) => emit('update:modelValue', v))

const sampleClaims = ref<string>(JSON.stringify({
  iss: 'https://login.microsoftonline.com/your-tenant/v2.0',
  sub: '00000000-0000-0000-0000-000000000001',
  email: 'alice@acme.com',
  preferred_username: 'alice',
  name: 'Alice Anderson',
  given_name: 'Alice',
  family_name: 'Anderson',
}, null, 2))

// Script-editor type hints: the input is the raw-claims dictionary and the
// script must return a partial user-record. Return-shape is intentionally
// narrow — Firstname/Lastname/Email/Acronym — to make it obvious what the
// script can and cannot touch.
const extraLibs = [{
  content: `
interface RawClaims {
  [key: string]: string | string[] | undefined;
}
interface UserUpdate {
  /** Patched onto User.Firstname. undefined = skip, null = clear, '' = skip. */
  firstname?: string | null;
  /** Patched onto User.Lastname. */
  lastname?: string | null;
  /** Patched onto User.Email. An existing different user owning this email rejects the login. */
  email?: string | null;
  /** Patched onto User.Acronym. */
  acronym?: string | null;
}
declare const claims: RawClaims;
`,
  filePath: 'file:///types/user-update.d.ts',
}]

function openTestDialog() {
  dialog.open(UserUpdateScriptTestDialog, {
    title: t('admin.loginProviders.testScript', {}, 'User-Update-Script testen'),
    size: 'l',
  }, {
    script: script.value,
    loginProviderId: props.loginProviderId,
    isNew: props.isNew,
    sampleClaims: sampleClaims.value,
    onSampleClaimsChange: (value: string) => { sampleClaims.value = value },
  })
}
</script>

<template>
  <div class="script-editor-layout">
    <div class="script-editor-toolbar">
      <span class="script-editor-title">
        {{ t('admin.loginProviders.userUpdateScript', {}, 'User-Update-Script') }}
      </span>
      <CoarButton size="s" icon-start="play" @click="openTestDialog">
        {{ t('admin.loginProviders.testScriptAction', {}, 'Script testen') }}
      </CoarButton>
    </div>

    <CoarScriptEditor
      v-model="script"
      :extra-libs="extraLibs"
      variant="inline"
      script-mode
      class="script-editor"
      placeholder="(claims) => ({ firstname: claims.given_name?.trim(), lastname: claims.family_name?.trim(), email: claims.email, acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '') })"
    />
  </div>
</template>

<style scoped>
.script-editor-layout {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  height: 100%;
  min-height: 0;
}

.script-editor-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}

.script-editor-title {
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.8rem;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.script-editor {
  flex: 1;
  min-height: 0;
}
</style>
