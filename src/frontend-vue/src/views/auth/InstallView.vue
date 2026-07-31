<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { HttpClientError, useHttpClient } from '@/composables/useHttpClient'
import {
  CoarButton,
  CoarCard,
  CoarFormField,
  CoarNotice,
  CoarPasswordInput,
  CoarTextInput,
} from '@cocoar/vue-ui'

interface CompleteResponse {
  RealmSlug: string
  PrimaryDomain: string
  LoginUrl: string
}

const route = useRoute()
const api = useHttpClient('/api/install')
const token = computed(() => typeof route.query.token === 'string' ? route.query.token : '')

const validating = ref(true)
const tokenValid = ref(false)
const submitting = ref(false)
const completed = ref<CompleteResponse | null>(null)
const error = ref('')

const realmSlug = ref('')
const displayName = ref('')
const domain = ref(window.location.hostname)
const userName = ref('')
const email = ref('')
const firstname = ref('')
const lastname = ref('')
const password = ref('')
const passwordConfirm = ref('')

const passwordsMatch = computed(() => password.value === passwordConfirm.value)
const canSubmit = computed(() =>
  token.value
  && tokenValid.value
  && realmSlug.value
  && displayName.value
  && domain.value
  && userName.value
  && email.value
  && password.value
  && passwordsMatch.value,
)

function detail(e: unknown): string {
  if (e instanceof HttpClientError
      && e.body
      && typeof e.body === 'object'
      && 'detail' in e.body
      && typeof e.body.detail === 'string') {
    return e.body.detail
  }
  return 'Die Installation konnte nicht abgeschlossen werden.'
}

onMounted(async () => {
  if (!token.value) {
    error.value = 'Der Installationslink enthält keinen Token.'
    validating.value = false
    return
  }

  try {
    const status = await api.addPath('status').get<{ IsInitialized: boolean }>()
    if (status.IsInitialized) {
      window.location.href = '/login'
      return
    }
    await api.addPath('validate').post({ Token: token.value })
    tokenValid.value = true
  } catch (e) {
    error.value = detail(e)
  } finally {
    validating.value = false
  }
})

async function submit() {
  if (!canSubmit.value || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    completed.value = await api.addPath('complete').post<CompleteResponse>({
      Token: token.value,
      Realm: {
        Slug: realmSlug.value,
        DisplayName: displayName.value,
        Description: null,
        Domains: [domain.value],
        PrimaryDomain: domain.value,
      },
      Admin: {
        UserName: userName.value,
        Email: email.value,
        Firstname: firstname.value || null,
        Lastname: lastname.value || null,
        Password: password.value,
      },
    })
  } catch (e) {
    error.value = detail(e)
  } finally {
    submitting.value = false
  }
}

function goToLogin() {
  if (completed.value)
    window.location.href = completed.value.LoginUrl
}
</script>

<template>
  <div class="min-h-screen bg-surface-50 px-4 py-10">
    <div class="mx-auto w-full max-w-3xl">
      <div class="mb-8 text-center">
        <div class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-2xl bg-[#525e76]/10 text-[#525e76]">
          <span class="text-2xl font-bold">M</span>
        </div>
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Modgud installieren
        </h1>
        <p class="mt-2 text-sm text-surface-500">
          Erster Realm und erster Administrator
        </p>
      </div>

      <CoarCard elevated>
        <CoarNotice v-if="validating" variant="info">
          Installationslink wird geprüft …
        </CoarNotice>

        <div v-else-if="completed" class="space-y-5">
          <CoarNotice variant="success">
            Die Installation wurde erfolgreich abgeschlossen. Der Realm
            „{{ completed.RealmSlug }}“ ist jetzt die Control Plane.
          </CoarNotice>
          <CoarButton full-width @click="goToLogin">
            Zur Anmeldung
          </CoarButton>
        </div>

        <div v-else-if="error && !tokenValid" class="space-y-4">
          <CoarNotice variant="error">{{ error }}</CoarNotice>
          <p class="text-sm text-surface-500">
            Erzeuge mit <code>recover install-link --base-url …</code> einen neuen Link.
          </p>
        </div>

        <form v-else class="space-y-7" @submit.prevent="submit">
          <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

          <section>
            <h2 class="mb-4 text-base font-semibold text-surface-800">Realm</h2>
            <div class="grid gap-4 md:grid-cols-2">
              <CoarFormField label="Anzeigename">
                <CoarTextInput v-model="displayName" required placeholder="Cocoar" />
              </CoarFormField>
              <CoarFormField label="Slug">
                <CoarTextInput v-model="realmSlug" required placeholder="cocoar" />
              </CoarFormField>
              <CoarFormField class="md:col-span-2" label="Primäre Domain">
                <CoarTextInput v-model="domain" required placeholder="auth.example.com" />
              </CoarFormField>
            </div>
          </section>

          <section>
            <h2 class="mb-4 text-base font-semibold text-surface-800">Erster Administrator</h2>
            <div class="grid gap-4 md:grid-cols-2">
              <CoarFormField label="Benutzername">
                <CoarTextInput v-model="userName" required autocomplete="username" />
              </CoarFormField>
              <CoarFormField label="E-Mail">
                <CoarTextInput v-model="email" required autocomplete="email" />
              </CoarFormField>
              <CoarFormField label="Vorname">
                <CoarTextInput v-model="firstname" autocomplete="given-name" />
              </CoarFormField>
              <CoarFormField label="Nachname">
                <CoarTextInput v-model="lastname" autocomplete="family-name" />
              </CoarFormField>
              <CoarFormField label="Passwort">
                <CoarPasswordInput v-model="password" required autocomplete="new-password" />
              </CoarFormField>
              <CoarFormField label="Passwort bestätigen">
                <CoarPasswordInput v-model="passwordConfirm" required autocomplete="new-password" />
              </CoarFormField>
            </div>
            <CoarNotice v-if="passwordConfirm && !passwordsMatch" class="mt-4" variant="error">
              Die Passwörter stimmen nicht überein.
            </CoarNotice>
          </section>

          <CoarButton
            type="submit"
            full-width
            :disabled="!canSubmit"
            :loading="submitting"
          >
            Realm erstellen und Installation abschließen
          </CoarButton>
        </form>
      </CoarCard>
    </div>
  </div>
</template>
