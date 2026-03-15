<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRoute } from 'vue-router';
import { CoarCard, CoarButton, CoarCheckbox, CoarNote } from '@cocoar/vue-ui';
import { http } from '@/core/api/http';
import { ApiError } from '@/core/api/http';

interface ConsentScopeInfo {
  name: string;
  displayName: string;
  description?: string;
  required: boolean;
}

interface ConsentModel {
  clientId: string;
  clientName: string;
  requestedScopes: ConsentScopeInfo[];
  returnUrl: string;
}

interface ConsentResult {
  redirectUrl: string;
}

const route = useRoute();

const consentInfo = ref<ConsentModel | null>(null);
const selectedScopes = ref<Set<string>>(new Set());
const isLoading = ref(true);
const isSubmitting = ref(false);
const error = ref('');

onMounted(async () => {
  const returnUrl = route.query.returnUrl as string;
  if (!returnUrl) {
    error.value = 'Invalid consent request. No return URL provided.';
    isLoading.value = false;
    return;
  }

  try {
    const data = await http.get<ConsentModel>(`/consent?returnUrl=${encodeURIComponent(returnUrl)}`);
    consentInfo.value = data;

    // Pre-select all scopes (required ones will be disabled)
    for (const scope of data.requestedScopes) {
      selectedScopes.value.add(scope.name);
    }
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to load consent information.';
  } finally {
    isLoading.value = false;
  }
});

function isScopeSelected(name: string): boolean {
  return selectedScopes.value.has(name);
}

function toggleScope(name: string, required: boolean) {
  if (required) return;
  const newSet = new Set(selectedScopes.value);
  if (newSet.has(name)) {
    newSet.delete(name);
  } else {
    newSet.add(name);
  }
  selectedScopes.value = newSet;
}

async function onAllow() {
  if (!consentInfo.value) return;
  isSubmitting.value = true;
  error.value = '';

  try {
    const result = await http.post<ConsentResult>('/consent', {
      approved: true,
      approvedScopes: Array.from(selectedScopes.value),
      returnUrl: consentInfo.value.returnUrl,
    });

    // Redirect back to the authorization endpoint to complete the flow
    window.location.href = result.redirectUrl;
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to process consent.';
    isSubmitting.value = false;
  }
}

async function onDeny() {
  if (!consentInfo.value) return;
  isSubmitting.value = true;
  error.value = '';

  try {
    const result = await http.post<ConsentResult>('/consent', {
      approved: false,
      approvedScopes: [],
      returnUrl: consentInfo.value.returnUrl,
    });

    window.location.href = result.redirectUrl;
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to process consent.';
    isSubmitting.value = false;
  }
}
</script>

<template>
  <div class="auth-page">
    <div class="auth-container">
      <div class="auth-brand">
        <span class="auth-brand-icon">&#9889;</span>
        <span class="auth-brand-name">Cocoar Auth</span>
      </div>

      <CoarCard elevated padding="l" class="auth-card">
        <!-- Loading state -->
        <div v-if="isLoading" class="loading-state">
          <p class="auth-subtitle">Loading consent information...</p>
        </div>

        <!-- Error state without consent info -->
        <div v-else-if="!consentInfo && error">
          <h1 class="auth-title">Authorization Error</h1>
          <CoarNote variant="error" padding="s" class="mb-4">{{ error }}</CoarNote>
        </div>

        <!-- Consent form -->
        <template v-else-if="consentInfo">
          <h1 class="auth-title">Permission Request</h1>
          <p class="auth-subtitle">
            <strong>{{ consentInfo.clientName }}</strong> is requesting access to your account.
          </p>

          <CoarNote v-if="error" variant="error" padding="s" class="mb-4">{{ error }}</CoarNote>

          <div class="scopes-section">
            <p class="scopes-label">This application would like to:</p>
            <div class="scopes-list">
              <div
                v-for="scope in consentInfo.requestedScopes"
                :key="scope.name"
                class="scope-item"
              >
                <CoarCheckbox
                  :model-value="isScopeSelected(scope.name)"
                  :label="scope.displayName"
                  :disabled="scope.required"
                  @update:model-value="toggleScope(scope.name, scope.required)"
                />
                <p v-if="scope.description" class="scope-description">{{ scope.description }}</p>
                <span v-if="scope.required" class="scope-required">Required</span>
              </div>
            </div>
          </div>

          <div class="consent-actions">
            <CoarButton
              variant="primary"
              :full-width="true"
              :loading="isSubmitting"
              :disabled="isSubmitting"
              @click="onAllow"
            >
              Allow
            </CoarButton>
            <CoarButton
              variant="secondary"
              :full-width="true"
              :disabled="isSubmitting"
              @click="onDeny"
            >
              Deny
            </CoarButton>
          </div>
        </template>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.auth-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
}

.auth-container {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 100%;
  max-width: 420px;
  gap: 1.25rem;
}

.auth-brand {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.auth-brand-icon {
  font-size: 1.5rem;
  line-height: 1;
}

.auth-brand-name {
  font-size: 1.125rem;
  font-weight: 700;
  color: var(--coar-text-neutral-primary);
  letter-spacing: -0.01em;
}

.auth-card {
  width: 100%;
}

:deep(.auth-card.coar-card--elevated) {
  box-shadow: 0 24px 64px rgba(0, 0, 0, 0.35), 0 8px 24px rgba(0, 0, 0, 0.2) !important;
}

.auth-title {
  margin: 0 0 0.375rem;
  font-size: 1.375rem;
  font-weight: 700;
  color: var(--coar-text-neutral-primary);
  letter-spacing: -0.02em;
}

.auth-subtitle {
  margin: 0 0 1.5rem;
  font-size: 0.875rem;
  color: var(--coar-text-neutral-secondary);
}

.loading-state {
  text-align: center;
  padding: 1rem 0;
}

.scopes-section {
  margin-bottom: 1.5rem;
}

.scopes-label {
  margin: 0 0 0.75rem;
  font-size: 0.875rem;
  font-weight: 600;
  color: var(--coar-text-neutral-primary);
}

.scopes-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.scope-item {
  position: relative;
  padding: 0.625rem 0.75rem;
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  border-radius: 0.375rem;
  background: var(--coar-bg-neutral-secondary, #f9fafb);
}

.scope-description {
  margin: 0.25rem 0 0 1.75rem;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary);
}

.scope-required {
  position: absolute;
  top: 0.625rem;
  right: 0.75rem;
  font-size: 0.6875rem;
  font-weight: 600;
  color: var(--coar-text-neutral-secondary);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.consent-actions {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.mb-4 {
  margin-bottom: 1rem;
}
</style>
