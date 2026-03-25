<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import {
  CoarCard, CoarButton, CoarNote, CoarTextInput, CoarPasswordInput,
  CoarSelect, CoarCheckbox, CoarSpinner, CoarCodeBlock, CoarTabGroup, CoarTab, useToast,
} from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import { parseLines } from '@/core/utils/text';
import DualListSelector from '@/components/DualListSelector.vue';
import ClaimsGrid from '@/components/ClaimsGrid.vue';
import type { Role } from '@/core/models/auth.models';
import type { OAuthScope, OAuthClientClaim } from '@/core/models/oauth.models';
import type { DualListItem } from '@/components/DualListSelector.vue';
import type { Claim } from '@/components/ClaimsGrid.vue';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();

const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);

// Loading / saving state
const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');
const newSecret = ref('');

// Tab navigation
const activeTab = ref<string>('basic');

// ---- Tab 1: Basic Information ----
const clientId = ref('');
const displayName = ref('');
const enabled = ref(true);
const accessTokenType = ref<'Reference' | 'Jwt'>('Jwt');
const refreshTokenUsage = ref<'OneTimeOnly' | 'ReUse'>('OneTimeOnly');
const allowAccessTokensViaBrowser = ref(false);
const requireClientSecret = ref(true);
const enableLocalLogin = ref(true);
const requireConsent = ref(false);
const allowRememberConsent = ref(true);
const allowedGrantTypes = ref<string[]>([]);
const clientSecret = ref('');

// Legacy fields kept for backward compat with current backend
const clientType = ref<'public' | 'confidential'>('confidential');
const consentType = ref<'explicit' | 'implicit' | 'external'>('explicit');

// ---- Tab 2: Static Role Membership ----
const roles = ref<Role[]>([]);
const selectedRoles = ref<string[]>([]);

const roleItems = computed<DualListItem[]>(() =>
  roles.value.map((r) => ({ id: r.id, name: r.name, displayName: r.description })),
);

// ---- Tab 3: URI Options ----
const redirectUris = ref('');
const postLogoutRedirectUris = ref('');
const allowedCorsOrigins = ref('');

// ---- Tab 4: Lifetime Options ----
const identityTokenLifetime = ref(3600);
const accessTokenLifetime = ref(3600);
const authorizationCodeLifetime = ref(300);
const absoluteRefreshTokenLifetime = ref(2592000);
const slidingRefreshTokenLifetime = ref(1296000);

// ---- Tab 5: Scopes ----
const availableScopes = ref<OAuthScope[]>([]);
const selectedScopes = ref<string[]>([]);

const scopeItems = computed<DualListItem[]>(() =>
  availableScopes.value.map((s) => ({ id: s.id, name: s.name, displayName: s.displayName, description: s.description })),
);

// ---- Tab 6: Claims ----
const claims = ref<Claim[]>([]);
const alwaysSendClientClaims = ref(false);
const updateAccessTokenClaimsOnRefresh = ref(false);
const clientClaimsPrefix = ref('client_');

// Grant type options
const grantTypeOptions = [
  { key: 'password', label: 'Password' },
  { key: 'implicit', label: 'Implicit' },
  { key: 'client_credentials', label: 'Client Credentials' },
  { key: 'authorization_code', label: 'Authorization Code' },
  { key: 'hybrid', label: 'Hybrid' },
];

// Mark dirty on any field change
watch([
  clientId, displayName, enabled, accessTokenType, refreshTokenUsage,
  allowAccessTokensViaBrowser, requireClientSecret, enableLocalLogin,
  requireConsent, allowRememberConsent, allowedGrantTypes, clientSecret,
  clientType, consentType, selectedRoles, redirectUris, postLogoutRedirectUris,
  allowedCorsOrigins, identityTokenLifetime, accessTokenLifetime,
  authorizationCodeLifetime, absoluteRefreshTokenLifetime, slidingRefreshTokenLifetime,
  selectedScopes, claims, alwaysSendClientClaims, updateAccessTokenClaimsOnRefresh,
  clientClaimsPrefix,
], () => {
  isDirty.value = true;
}, { deep: true });

function toggleGrantType(key: string) {
  const idx = allowedGrantTypes.value.indexOf(key);
  if (idx >= 0) {
    allowedGrantTypes.value = allowedGrantTypes.value.filter((g) => g !== key);
  } else {
    allowedGrantTypes.value = [...allowedGrantTypes.value, key];
  }
}

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit OAuth Client' : 'Create OAuth Client';
  ctx.header.subTitle = isEditMode.value ? 'Update client configuration' : 'Register a new OAuth 2.0 client';
  ctx.content.scrollable = true;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/oauth/clients');
  ctx.footer.button2.visible = isEditMode.value;
  ctx.footer.button2.text = 'Delete';
  ctx.footer.button2.onClick = () => onDeleteClient();
  ctx.footer.button3.visible = true;
  ctx.footer.button3.text = isEditMode.value ? 'Save Changes' : 'Create';
  ctx.footer.button3.onClick = () => onSubmit();
});

watch(isSaving, (val) => { ui.state.footer.button3.loading = val; });

const toast = useToast();

async function onDeleteClient() {
  if (!confirm('Are you sure you want to delete this OAuth client?')) return;
  try {
    await adminApi.deleteOAuthClient(id.value!);
    isDirty.value = false;
    toast.success('OAuth client deleted.');
    router.push('/admin/oauth/clients');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to delete OAuth client.';
  }
}

onMounted(async () => {
  isLoading.value = true;
  error.value = '';
  try {
    // Load roles and scopes in parallel, plus client data if editing
    const [rolesResult, scopesResult, clientData] = await Promise.all([
      adminApi.getRoles(),
      adminApi.getOAuthScopes(),
      isEditMode.value ? adminApi.getOAuthClient(id.value!) : Promise.resolve(null),
    ]);

    roles.value = rolesResult.items;
    availableScopes.value = scopesResult.items;

    if (clientData) {
      clientId.value = clientData.clientId;
      displayName.value = clientData.displayName || '';
      clientType.value = clientData.clientType;
      consentType.value = clientData.consentType;
      redirectUris.value = clientData.redirectUris.join('\n');
      postLogoutRedirectUris.value = clientData.postLogoutRedirectUris.join('\n');
      selectedScopes.value = clientData.permissions || [];

      enabled.value = clientData.enabled ?? true;
      accessTokenType.value = clientData.accessTokenType ?? 'Jwt';
      refreshTokenUsage.value = clientData.refreshTokenUsage ?? 'OneTimeOnly';
      allowAccessTokensViaBrowser.value = clientData.allowAccessTokensViaBrowser ?? false;
      requireClientSecret.value = clientData.requireClientSecret ?? true;
      enableLocalLogin.value = clientData.enableLocalLogin ?? true;
      requireConsent.value = clientData.requireConsent ?? false;
      allowRememberConsent.value = clientData.allowRememberConsent ?? true;
      allowedGrantTypes.value = clientData.allowedGrantTypes ?? [];
      allowedCorsOrigins.value = (clientData.allowedCorsOrigins ?? []).join('\n');
      identityTokenLifetime.value = clientData.identityTokenLifetime ?? 3600;
      accessTokenLifetime.value = clientData.accessTokenLifetime ?? 3600;
      authorizationCodeLifetime.value = clientData.authorizationCodeLifetime ?? 300;
      absoluteRefreshTokenLifetime.value = clientData.absoluteRefreshTokenLifetime ?? 2592000;
      slidingRefreshTokenLifetime.value = clientData.slidingRefreshTokenLifetime ?? 1296000;
      selectedRoles.value = clientData.roles ?? [];
      alwaysSendClientClaims.value = clientData.alwaysSendClientClaims ?? false;
      updateAccessTokenClaimsOnRefresh.value = clientData.updateAccessTokenClaimsOnRefresh ?? false;
      clientClaimsPrefix.value = clientData.clientClaimsPrefix ?? 'client_';
      claims.value = (clientData.claims ?? []).map((c) => ({ type: c.type, value: c.value }));
    }
  } catch {
    error.value = 'Failed to load data.';
  } finally {
    isLoading.value = false;
    setTimeout(() => { isDirty.value = false; }, 0);
  }
});

async function onSubmit() {
  isSaving.value = true;
  error.value = '';
  try {
    if (isEditMode.value) {
      await adminApi.updateOAuthClient(id.value!, {
        displayName: displayName.value || undefined,
        consentType: consentType.value,
        redirectUris: parseLines(redirectUris.value),
        postLogoutRedirectUris: parseLines(postLogoutRedirectUris.value),
        scopes: selectedScopes.value,

        enabled: enabled.value,
        accessTokenType: accessTokenType.value,
        refreshTokenUsage: refreshTokenUsage.value,
        allowAccessTokensViaBrowser: allowAccessTokensViaBrowser.value,
        requireClientSecret: requireClientSecret.value,
        enableLocalLogin: enableLocalLogin.value,
        requireConsent: requireConsent.value,
        allowRememberConsent: allowRememberConsent.value,
        allowedGrantTypes: allowedGrantTypes.value,
        allowedCorsOrigins: parseLines(allowedCorsOrigins.value),
        identityTokenLifetime: identityTokenLifetime.value,
        accessTokenLifetime: accessTokenLifetime.value,
        authorizationCodeLifetime: authorizationCodeLifetime.value,
        absoluteRefreshTokenLifetime: absoluteRefreshTokenLifetime.value,
        slidingRefreshTokenLifetime: slidingRefreshTokenLifetime.value,
        roles: selectedRoles.value,
        alwaysSendClientClaims: alwaysSendClientClaims.value,
        updateAccessTokenClaimsOnRefresh: updateAccessTokenClaimsOnRefresh.value,
        clientClaimsPrefix: clientClaimsPrefix.value,
        claims: claims.value.filter((c) => c.type),
      });
      isDirty.value = false;
      router.push('/admin/oauth/clients');
    } else {
      const result = await adminApi.createOAuthClient({
        clientId: clientId.value,
        displayName: displayName.value || undefined,
        clientType: clientType.value,
        clientSecret: clientSecret.value || undefined,
        consentType: consentType.value,
        redirectUris: parseLines(redirectUris.value),
        postLogoutRedirectUris: parseLines(postLogoutRedirectUris.value),
        scopes: selectedScopes.value,

        enabled: enabled.value,
        accessTokenType: accessTokenType.value,
        refreshTokenUsage: refreshTokenUsage.value,
        allowAccessTokensViaBrowser: allowAccessTokensViaBrowser.value,
        requireClientSecret: requireClientSecret.value,
        enableLocalLogin: enableLocalLogin.value,
        requireConsent: requireConsent.value,
        allowRememberConsent: allowRememberConsent.value,
        allowedGrantTypes: allowedGrantTypes.value,
        allowedCorsOrigins: parseLines(allowedCorsOrigins.value),
        identityTokenLifetime: identityTokenLifetime.value,
        accessTokenLifetime: accessTokenLifetime.value,
        authorizationCodeLifetime: authorizationCodeLifetime.value,
        absoluteRefreshTokenLifetime: absoluteRefreshTokenLifetime.value,
        slidingRefreshTokenLifetime: slidingRefreshTokenLifetime.value,
        roles: selectedRoles.value,
        alwaysSendClientClaims: alwaysSendClientClaims.value,
        updateAccessTokenClaimsOnRefresh: updateAccessTokenClaimsOnRefresh.value,
        clientClaimsPrefix: clientClaimsPrefix.value,
        claims: claims.value.filter((c) => c.type),
      });
      isDirty.value = false;
      if (result.clientSecret) {
        newSecret.value = result.clientSecret;
      } else {
        router.push('/admin/oauth/clients');
      }
    }
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save client.';
  } finally {
    isSaving.value = false;
  }
}


</script>

<template>
  <div class="form-page">
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>

    <!-- Secret display after creation -->
    <template v-else-if="newSecret">
      <CoarNote variant="warning" padding="s" class="mb-3">
        Save this client secret now — it will not be shown again.
      </CoarNote>
      <CoarCard padding="l" class="form-card">
        <CoarCodeBlock :code="newSecret" language="text" />
        <CoarButton variant="primary" class="mt-3" @click="router.push('/admin/oauth/clients')">Done</CoarButton>
      </CoarCard>
    </template>

    <template v-else>
      <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>

      <form @submit.prevent="onSubmit">
        <CoarTabGroup v-model="activeTab">
          <CoarTab id="basic">
            <template #default>Basic Information</template>
            <template #content>
              <div class="form-layout">
                <!-- Left column (~70%): Main form fields -->
                <div class="form-main">
                  <CoarCard padding="l" class="form-card">
                    <!-- Row: ClientId + ClientName -->
                    <div class="form-row-2">
                      <CoarTextInput v-model="clientId" label="Client ID" :required="true" :disabled="isEditMode" />
                      <CoarTextInput v-model="displayName" label="Client Name" />
                    </div>

                    <!-- Token options box -->
                    <div class="options-box mb-3">
                      <div class="options-box-section">
                        <label class="field-label">Access Token Type</label>
                        <div class="radio-group">
                          <label class="radio-label">
                            <input type="radio" v-model="accessTokenType" value="Reference" />
                            Reference
                          </label>
                          <label class="radio-label">
                            <input type="radio" v-model="accessTokenType" value="Jwt" />
                            JWT
                          </label>
                        </div>
                      </div>
                      <div class="options-box-section">
                        <label class="field-label">Refresh Token Usage</label>
                        <div class="radio-group">
                          <label class="radio-label">
                            <input type="radio" v-model="refreshTokenUsage" value="OneTimeOnly" />
                            OneTimeOnly
                          </label>
                          <label class="radio-label">
                            <input type="radio" v-model="refreshTokenUsage" value="ReUse" />
                            ReUse
                          </label>
                        </div>
                      </div>
                      <div class="options-box-section">
                        <CoarCheckbox v-model="allowAccessTokensViaBrowser" label="Allow AccessTokens via Browser" />
                      </div>
                    </div>

                    <!-- Client Type / Consent Type (legacy fields) -->
                    <div class="form-row-2">
                      <CoarSelect v-model="clientType" label="Client Type" :options="[
                        { value: 'confidential', label: 'Confidential' },
                        { value: 'public', label: 'Public' },
                      ]" :disabled="isEditMode" />
                      <CoarSelect v-model="consentType" label="Consent Type" :options="[
                        { value: 'explicit', label: 'Explicit' },
                        { value: 'implicit', label: 'Implicit' },
                        { value: 'external', label: 'External' },
                      ]" />
                    </div>

                    <!-- Client Secret for create mode -->
                    <div v-if="!isEditMode && clientType === 'confidential'" class="form-group">
                      <CoarPasswordInput v-model="clientSecret" label="Client Secret (leave empty to auto-generate)" />
                    </div>
                  </CoarCard>
                </div>

                <!-- Right sidebar (~30%): Options -->
                <div class="form-sidebar">
                  <CoarCard padding="l" class="form-card">
                    <h2 class="section-title">Options</h2>
                    <div class="sidebar-checks">
                      <CoarCheckbox v-model="enabled" label="Enabled" />
                      <CoarCheckbox v-model="requireClientSecret" label="Require Client Secret" />
                      <CoarCheckbox v-model="enableLocalLogin" label="Enable Local Login" />
                      <CoarCheckbox v-model="requireConsent" label="Require Consent" />
                      <CoarCheckbox v-model="allowRememberConsent" label="Allow Remember Consent" />
                    </div>

                    <h2 class="section-title mt-3">Allowed Grant Types</h2>
                    <div class="sidebar-checks">
                      <CoarCheckbox
                        v-for="gt in grantTypeOptions"
                        :key="gt.key"
                        :model-value="allowedGrantTypes.includes(gt.key)"
                        :label="gt.label"
                        @update:model-value="toggleGrantType(gt.key)"
                      />
                    </div>
                  </CoarCard>
                </div>
              </div>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="roles">
            <template #default>Static Role Membership</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <h2 class="section-title">Static Role Membership</h2>
                <CoarNote variant="info" padding="s" class="mb-3">
                  Assign roles directly to this client application. Roles will be included in tokens issued to this client.
                </CoarNote>
                <DualListSelector
                  v-model="selectedRoles"
                  :items="roleItems"
                  assigned-label="Member of following Roles"
                  available-label="Available Roles"
                  filter-placeholder="Filter roles..."
                />
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="uris">
            <template #default>URI Options</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <h2 class="section-title">URI Options</h2>
                <div class="form-group">
                  <CoarTextInput v-model="redirectUris" label="Redirect URIs (one per line)" :rows="5" />
                </div>
                <div class="form-group">
                  <CoarTextInput v-model="postLogoutRedirectUris" label="Post-Logout Redirect URIs (one per line)" :rows="5" />
                </div>
                <div class="form-group">
                  <CoarTextInput v-model="allowedCorsOrigins" label="Allowed CORS Origins (one per line)" :rows="5" />
                </div>
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="lifetimes">
            <template #default>Lifetime Options</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <h2 class="section-title">Lifetime Options</h2>
                <CoarNote variant="info" padding="s" class="mb-3">
                  All values are in seconds.
                </CoarNote>
                <div class="lifetime-grid">
                  <div class="lifetime-row">
                    <label class="lifetime-label">Identity Token Lifetime</label>
                    <div class="lifetime-input">
                      <CoarTextInput v-model.number="identityTokenLifetime" type="number" />
                    </div>
                  </div>
                  <div class="lifetime-row">
                    <label class="lifetime-label">Access Token Lifetime</label>
                    <div class="lifetime-input">
                      <CoarTextInput v-model.number="accessTokenLifetime" type="number" />
                    </div>
                  </div>
                  <div class="lifetime-row">
                    <label class="lifetime-label">Authorization Code Lifetime</label>
                    <div class="lifetime-input">
                      <CoarTextInput v-model.number="authorizationCodeLifetime" type="number" />
                    </div>
                  </div>
                  <div class="lifetime-row">
                    <label class="lifetime-label">Absolute Refresh Token Lifetime</label>
                    <div class="lifetime-input">
                      <CoarTextInput v-model.number="absoluteRefreshTokenLifetime" type="number" />
                    </div>
                  </div>
                  <div class="lifetime-row">
                    <label class="lifetime-label">Sliding Refresh Token Lifetime</label>
                    <div class="lifetime-input">
                      <CoarTextInput v-model.number="slidingRefreshTokenLifetime" type="number" />
                    </div>
                  </div>
                </div>
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="scopes">
            <template #default>Scopes</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <h2 class="section-title">Scopes</h2>
                <DualListSelector
                  v-model="selectedScopes"
                  :items="scopeItems"
                  assigned-label="Assigned Scopes"
                  available-label="Available Scopes"
                  filter-placeholder="Filter scopes..."
                />
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="claims">
            <template #default>Claims</template>
            <template #content>
              <div class="form-layout">
                <!-- Left column: Claims grid -->
                <div class="form-main">
                  <CoarCard padding="l" class="form-card">
                    <h2 class="section-title">Client Claims</h2>
                    <CoarNote variant="info" padding="s" class="mb-3">
                      Client claims are included in tokens issued to this client.
                    </CoarNote>
                    <ClaimsGrid v-model="claims" />
                  </CoarCard>
                </div>

                <!-- Right sidebar: Claims options -->
                <div class="form-sidebar">
                  <CoarCard padding="l" class="form-card">
                    <h2 class="section-title">Options</h2>
                    <div class="sidebar-checks">
                      <CoarCheckbox v-model="alwaysSendClientClaims" label="Always Send Client Claims" />
                      <CoarCheckbox v-model="updateAccessTokenClaimsOnRefresh" label="Update Claims on Refresh" />
                    </div>
                    <div class="form-group mt-3">
                      <CoarTextInput v-model="clientClaimsPrefix" label="Claims Prefix" placeholder="client_" />
                    </div>
                  </CoarCard>
                </div>
              </div>
            </template>
          </CoarTab>
        </CoarTabGroup>
      </form>
    </template>
  </div>
</template>

<style scoped>
.form-page { }

/* 2-column layout */
.form-layout { display: grid; grid-template-columns: 1fr 300px; gap: 1.5rem; align-items: start; }
@media (max-width: 860px) {
  .form-layout { grid-template-columns: 1fr; }
}
.form-main { min-width: 0; }
.form-sidebar { min-width: 0; }

.section-title { margin: 0 0 1rem; font-size: 1rem; font-weight: 600; }

/* Form fields */
.form-group { margin-bottom: 1rem; }
.form-group:last-child { margin-bottom: 0; }
.form-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; margin-bottom: 1rem; }

.field-label { display: block; font-size: 0.8125rem; font-weight: 600; color: var(--coar-text-neutral-secondary); margin-bottom: 0.5rem; }
.sidebar-checks { display: flex; flex-direction: column; gap: 0.625rem; }

/* Options box (token type / refresh / browser toggle) */
.options-box {
  display: grid;
  grid-template-columns: 1fr 1fr 1fr;
  gap: 1rem;
  padding: 1rem;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: var(--coar-radius-m);
  background: var(--coar-background-neutral-secondary, var(--coar-background-neutral-primary));
}
@media (max-width: 640px) {
  .options-box { grid-template-columns: 1fr; }
}
.options-box-section { display: flex; flex-direction: column; gap: 0.375rem; }

/* Radio buttons */
.radio-group { display: flex; flex-direction: column; gap: 0.25rem; }
.radio-label {
  display: flex;
  align-items: center;
  gap: 0.375rem;
  font-size: 0.875rem;
  color: var(--coar-text-neutral-primary);
  cursor: pointer;
}
.radio-label input[type="radio"] { margin: 0; }

/* Lifetime grid */
.lifetime-grid { display: flex; flex-direction: column; gap: 0; }
.lifetime-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.75rem 0;
  border-bottom: 1px solid var(--coar-border-neutral-tertiary);
  gap: 1rem;
}
.lifetime-row:last-child { border-bottom: none; }
.lifetime-label {
  font-size: 0.9375rem;
  font-weight: 500;
  color: var(--coar-text-neutral-primary);
  flex-shrink: 0;
}
.lifetime-input { width: 180px; flex-shrink: 0; }

.form-actions { display: flex; gap: 0.75rem; }
.mb-3 { margin-bottom: 0.75rem; }
.mt-3 { margin-top: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
</style>
