<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarCheckbox,
  CoarSpinner,
} from '@cocoar/vue-ui'
import Notice from '@/components/Notice.vue'
import type {
  ConsentModel,
  ConsentDecision,
  ConsentResult,
} from '@/models/consent'

const { t, language } = useI18n()
const localization = useLocalization()!
const route = useRoute()
const router = useRouter()
const consentHttp = useHttpClient('/connect/consent')

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

type Phase = 'loading' | 'prompt' | 'denied' | 'error'
const phase = ref<Phase>('loading')
const error = ref('')
const submitting = ref(false)

// A dead ticket (expired/consumed) is not a dead end: the backend hands us
// a retry URL (/connect/authorize + the locked-in query) that safely mints a
// fresh ticket — or completes silently via the remembered authorization.
const retryUrl = ref<string | null>(null)

function readRetryUrl(e: HttpClientError): string | null {
  const b = e.body
  if (b && typeof b === 'object' && 'retryUrl' in b && typeof b.retryUrl === 'string'
    && b.retryUrl.startsWith('/connect/authorize')) {
    return b.retryUrl
  }
  return null
}

function retryAuthorize() {
  // Server-side endpoint — Vue Router cannot navigate there.
  if (retryUrl.value) window.location.assign(retryUrl.value)
}

const model = ref<ConsentModel | null>(null)
// approval is keyed by scope-name; required scopes are pre-checked AND
// the toggle is rendered disabled so the user cannot uncheck them.
const approvedScopes = ref<Record<string, boolean>>({})

// Some scopes have well-known display strings that we want to show
// instead of the raw `Name`/`DisplayName`. The backend already serves
// the right values for non-OIDC-standard scopes, so this is just a
// fallback for the OIDC core where the server may not have a
// localised display name configured.
const standardScopeFallbacks: Record<string, { label: string; description: string }> = {
  openid: {
    label: t('consent.scope.openid.label', {}, 'Sign-in identity'),
    description: t('consent.scope.openid.description', {}, 'Confirms who you are. Required to sign in.'),
  },
  profile: {
    label: t('consent.scope.profile.label', {}, 'Profile'),
    description: t('consent.scope.profile.description', {}, 'Name, picture, locale.'),
  },
  email: {
    label: t('consent.scope.email.label', {}, 'Email address'),
    description: t('consent.scope.email.description', {}, 'Your email address and whether it is verified.'),
  },
  offline_access: {
    label: t('consent.scope.offline_access.label', {}, 'Stay signed in'),
    description: t('consent.scope.offline_access.description', {}, 'Allows the app to refresh its access without prompting again.'),
  },
  roles: {
    label: t('consent.scope.roles.label', {}, 'Roles'),
    description: t('consent.scope.roles.description', {}, 'Lets the app see which roles you have in this realm.'),
  },
  permissions: {
    label: t('consent.scope.permissions.label', {}, 'Permissions'),
    description: t('consent.scope.permissions.description', {}, 'Lets the app see which fine-grained permissions you have.'),
  },
}

const ticket = computed(() => (route.query.ticket as string | undefined) ?? '')

onMounted(async () => {
  if (!ticket.value) {
    phase.value = 'error'
    error.value = t('consent.missingTicket', {}, 'Invalid consent link — no ticket.')
    return
  }
  try {
    const dto = await consentHttp.setQueryParameter('ticket', ticket.value).get<ConsentModel>()
    model.value = dto
    // Pre-check every scope by default — standard OAuth UX is
    // "approve everything that's asked", the user opts out.
    for (const scope of dto.RequestedScopes) {
      approvedScopes.value[scope.Name] = true
    }
    phase.value = 'prompt'
  } catch (e) {
    phase.value = 'error'
    if (e instanceof HttpClientError) {
      switch (e.status) {
        case 401:
          // Not authenticated — bounce to login with a redirect back.
          router.replace(`/login?redirect=${encodeURIComponent(route.fullPath)}`)
          return
        case 403:
          error.value = t('consent.forbidden', {}, 'This consent ticket belongs to a different user. Please sign in with the correct account.')
          break
        case 404:
          error.value = t('consent.notFound', {}, 'Consent request not found or expired. Please start the sign-in flow again from the app.')
          break
        case 409:
          error.value = t('consent.alreadyUsed', {}, 'This consent request has already been completed. Please start over from the app.')
          retryUrl.value = readRetryUrl(e)
          break
        case 400:
          error.value = t('consent.expired', {}, 'Consent request expired. Please start over from the app.')
          retryUrl.value = readRetryUrl(e)
          break
        default:
          error.value = t('consent.loadError', {}, 'Failed to load the consent request.')
      }
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
  }
})

async function submit(approved: boolean) {
  if (!model.value || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    const decision: ConsentDecision = {
      Ticket: model.value.Ticket,
      Approved: approved,
      ApprovedScopes: approved
        ? model.value.RequestedScopes
            .filter((s) => approvedScopes.value[s.Name])
            .map((s) => s.Name)
        : [],
    }
    const result = await consentHttp.post<ConsentResult>(decision)
    if (approved) {
      // Result.RedirectUrl points at /connect/authorize?<query> — a
      // server-side endpoint Vue Router cannot navigate to. Full-page
      // assign so the OIDC dance continues on the backend.
      window.location.assign(result.RedirectUrl)
    } else if (result.ReturnsToClient) {
      // Deny — the backend re-enters /connect/authorize with a deny marker;
      // OpenIddict then emits the RFC 6749 access_denied error to the client's
      // redirect_uri (honoring its response_mode + iss). RedirectUrl is a
      // same-origin /connect/authorize URL, so full-page-assign into it.
      window.location.assign(result.RedirectUrl)
    } else {
      // Defensive fallback: no client redirect available — render the denial
      // state inline (not reached while the backend always re-enters authorize).
      phase.value = 'denied'
    }
  } catch (e) {
    if (e instanceof HttpClientError && (e.status === 409 || e.status === 400)) {
      error.value = e.status === 409
        ? t('consent.alreadyUsed', {}, 'This consent request has already been completed. Please start over from the app.')
        : t('consent.expired', {}, 'Consent request expired. Please start over from the app.')
      retryUrl.value = readRetryUrl(e)
      phase.value = 'error'
    } else if (e instanceof HttpClientError && (e.status === 404 || e.status === 403)) {
      // Ticket GC'd between the prompt and submit, or bound to a different
      // user — not retryable in place, so surface the error card instead of
      // leaving the stale Allow/Deny prompt showing a dead message.
      error.value = e.status === 404
        ? t('consent.notFound', {}, 'Consent request not found or expired. Please start the sign-in flow again from the app.')
        : t('consent.forbidden', {}, 'This consent ticket belongs to a different user. Please sign in with the correct account.')
      phase.value = 'error'
    } else if (e instanceof HttpClientError) {
      error.value = t('consent.submitError', {}, 'Could not submit your decision. Please try again.')
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
  } finally {
    submitting.value = false
  }
}

function scopeLabel(name: string, fallback: string): string {
  const known = standardScopeFallbacks[name]
  return known?.label ?? (fallback || name)
}

function scopeDescription(name: string, fallback: string | null | undefined): string | null {
  if (fallback && fallback.trim()) return fallback
  return standardScopeFallbacks[name]?.description ?? null
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-surface-50 p-4 relative">
    <button
      class="absolute top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>
    <div class="w-full max-w-md">
      <div class="mb-8 text-center">
        <img src="/idp-logo.svg" alt="Modgud" class="mx-auto mb-1 h-16 w-auto" />
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Modgud
        </h1>
      </div>

      <CoarCard elevated>
        <!-- Loading -->
        <div v-if="phase === 'loading'" class="flex flex-col items-center gap-3 p-4 text-center">
          <CoarSpinner />
          <p class="text-sm text-surface-500">{{ t('common.loading', {}, 'Loading...') }}</p>
        </div>

        <!-- Error -->
        <div v-else-if="phase === 'error'" class="space-y-4">
          <Notice variant="error">{{ error }}</Notice>
          <!-- Expired/consumed tickets carry a retry URL — re-entering
               /connect/authorize mints a fresh ticket (or completes silently
               via the remembered authorization), so the OIDC flow resumes
               instead of dead-ending here. -->
          <CoarButton v-if="retryUrl" full-width @click="retryAuthorize">
            {{ t('consent.retry', {}, 'Try again') }}
          </CoarButton>
          <CoarButton :variant="retryUrl ? 'secondary' : undefined" full-width @click="router.push('/login')">
            {{ t('consent.toLogin', {}, 'Back to sign-in') }}
          </CoarButton>
        </div>

        <!-- Denied -->
        <div v-else-if="phase === 'denied'" class="space-y-4 text-center">
          <h2 class="text-lg font-semibold text-surface-800">
            {{ t('consent.deniedTitle', {}, 'Access denied') }}
          </h2>
          <p class="text-sm text-surface-600">
            {{ t('consent.deniedBody', { client: model?.ClientName ?? '' }, 'You did not authorise {client}. You can close this page or sign in again.') }}
          </p>
          <CoarButton full-width @click="router.push('/login')">
            {{ t('consent.toLogin', {}, 'Back to sign-in') }}
          </CoarButton>
        </div>

        <!-- Prompt -->
        <div v-else class="space-y-4">
          <div class="text-center">
            <h2 class="text-lg font-semibold text-surface-800">
              <template v-if="model!.IsDynamicallyRegistered">
                {{ t('consent.title', { client: model!.ClientName }, 'Authorise {client}') }}
                <span class="unverified-tag">[{{ t('consent.unverified', {}, 'unverified') }}]</span>
              </template>
              <template v-else>
                {{ t('consent.title', { client: model!.ClientName }, 'Authorise {client}') }}
              </template>
            </h2>
            <p class="mt-1 text-sm text-surface-500">
              {{ t('consent.subtitle', {}, 'Review the access this app is asking for.') }}
            </p>
            <p v-if="model!.ClientIdHostname" class="mt-2 text-sm text-surface-600">
              {{ t('consent.appIdentity', {}, 'App identity') }}:
              <span class="cimd-hostname">{{ model!.ClientIdHostname }}</span>
            </p>
          </div>

          <Notice truncate v-if="model!.ClientIdHostname" variant="warning">
            {{ t('consent.cimdWarningShort', { host: model!.ClientIdHostname }, 'This app is identified by the domain {host} — make sure you trust it.') }}
            <template #details>
              {{ t('consent.cimdWarning', { host: model!.ClientIdHostname }, 'This app is identified by the domain {host}. Make sure you trust this domain before continuing — only authorise it if you intended to sign in to an app at {host}.') }}
            </template>
          </Notice>
          <Notice truncate v-else-if="model!.IsDynamicallyRegistered" variant="warning">
            {{ t('consent.dcrWarningShort', {}, 'This app self-registered and its name is unverified.') }}
            <template #details>
              {{ t('consent.dcrWarning', {}, 'This app registered itself with the identity provider — its name has not been verified by an administrator. Make sure the name above matches the app you actually intended to authorise before continuing.') }}
            </template>
          </Notice>

          <div class="space-y-2">
            <div
              v-for="scope in model!.RequestedScopes"
              :key="scope.Name"
              class="rounded border border-surface-200 bg-white px-3 py-2"
            >
              <div class="flex items-start gap-3">
                <CoarCheckbox
                  v-model="approvedScopes[scope.Name]"
                  :disabled="scope.Required"
                  :label="scopeLabel(scope.Name, scope.DisplayName)"
                />
              </div>
              <p
                v-if="scopeDescription(scope.Name, scope.Description)"
                class="ml-7 mt-1 text-xs text-surface-500"
              >
                {{ scopeDescription(scope.Name, scope.Description) }}
              </p>
              <p v-if="scope.Required" class="ml-7 mt-1 text-[11px] text-surface-400">
                {{ t('consent.required', {}, 'Required') }}
              </p>
            </div>
          </div>

          <Notice v-if="error" variant="error">{{ error }}</Notice>

          <div class="flex gap-2">
            <CoarButton
              variant="secondary"
              :disabled="submitting"
              full-width
              @click="submit(false)"
            >
              {{ t('consent.deny', {}, 'Deny') }}
            </CoarButton>
            <CoarButton
              :loading="submitting"
              full-width
              @click="submit(true)"
            >
              {{ t('consent.allow', {}, 'Allow') }}
            </CoarButton>
          </div>
        </div>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.unverified-tag {
  display: inline-block;
  margin-left: 0.4em;
  font-size: 0.7em;
  font-weight: 500;
  letter-spacing: 0.05em;
  color: var(--coar-text-semantic-warning, #92400e);
  vertical-align: middle;
}

.cimd-hostname {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-weight: 600;
  color: var(--coar-text-neutral-primary, #1f2937);
}
</style>
