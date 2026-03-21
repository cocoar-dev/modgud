<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { CoarCard, CoarNote, CoarTextInput, CoarSelect, CoarSpinner, CoarTabGroup, CoarTab } from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import type { LoginProviderType } from '@/core/models/login-provider.models';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();

const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);

const name = ref('');
const displayName = ref('');
const description = ref('');
const type = ref<LoginProviderType>('Internal');

// OIDC configuration fields
const authority = ref('');
const clientId = ref('');
const clientSecret = ref('');
const scopes = ref('openid profile email');

const activeTab = ref<'basic' | 'configuration'>('basic');

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

const typeOptions = [
  { value: 'Internal', label: 'Internal' },
  { value: 'OpenIdConnect', label: 'OpenID Connect' },
];

const isOidc = computed(() => type.value === 'OpenIdConnect');

const configValidationError = computed(() => {
  if (!isOidc.value) return '';
  if (!authority.value) return 'Authority is required for OIDC providers.';
  if (!clientId.value) return 'Client ID is required for OIDC providers.';
  return '';
});

watch([name, displayName, description, type, authority, clientId, clientSecret, scopes], () => {
  isDirty.value = true;
});

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit Login Provider' : 'Create Login Provider';
  ctx.header.subTitle = isEditMode.value ? 'Update provider configuration' : 'Add a new authentication provider';
  ctx.content.scrollable = true;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/login-providers');
  ctx.footer.button3.visible = true;
  ctx.footer.button3.text = isEditMode.value ? 'Save Changes' : 'Create';
  ctx.footer.button3.onClick = () => onSubmit();
});

watch(isSaving, (val) => { ui.state.footer.button3.loading = val; });

onMounted(async () => {
  if (!isEditMode.value) return;
  isLoading.value = true;
  try {
    const provider = await adminApi.getLoginProvider(id.value!);
    name.value = provider.name;
    displayName.value = provider.displayName || '';
    description.value = provider.description || '';
    type.value = provider.type;
    // Populate OIDC fields from configuration object
    if (provider.configuration) {
      authority.value = provider.configuration['Authority'] || '';
      clientId.value = provider.configuration['ClientId'] || '';
      clientSecret.value = provider.configuration['ClientSecret'] || '';
      scopes.value = provider.configuration['Scopes'] || 'openid profile email';
    }
  } catch {
    error.value = 'Failed to load login provider.';
  } finally {
    isLoading.value = false;
    setTimeout(() => { isDirty.value = false; }, 0);
  }
});

function buildConfiguration(): Record<string, string> | undefined {
  if (!isOidc.value) return undefined;
  const config: Record<string, string> = {};
  if (authority.value) config['Authority'] = authority.value;
  if (clientId.value) config['ClientId'] = clientId.value;
  if (clientSecret.value) config['ClientSecret'] = clientSecret.value;
  if (scopes.value) config['Scopes'] = scopes.value;
  return Object.keys(config).length > 0 ? config : undefined;
}

async function onSubmit() {
  if (!name.value) return;
  if (isOidc.value && configValidationError.value) {
    error.value = configValidationError.value;
    activeTab.value = 'configuration';
    return;
  }
  isSaving.value = true;
  error.value = '';
  try {
    if (isEditMode.value) {
      await adminApi.updateLoginProvider(id.value!, {
        displayName: displayName.value || undefined,
        description: description.value || null,
        configuration: isOidc.value ? (buildConfiguration() ?? null) : null,
      });
    } else {
      await adminApi.createLoginProvider({
        name: name.value,
        displayName: displayName.value || undefined,
        description: description.value || undefined,
        type: type.value,
        configuration: buildConfiguration(),
      });
    }
    isDirty.value = false;
    router.push('/admin/login-providers');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save login provider.';
  } finally {
    isSaving.value = false;
  }
}
</script>

<template>
  <div class="form-page">
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>

    <template v-else>
      <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>

      <form @submit.prevent="onSubmit">
        <CoarTabGroup v-model="activeTab">
          <CoarTab id="basic">
            <template #default>Basic Information</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <div class="form-group">
                  <CoarTextInput v-model="name" label="Name" :required="true" :disabled="isEditMode" />
                </div>
                <div class="form-group">
                  <CoarTextInput v-model="displayName" label="Display Name" />
                </div>
                <div class="form-group">
                  <CoarTextInput v-model="description" label="Description" :rows="3" />
                </div>
                <div class="form-group">
                  <CoarSelect v-model="type" label="Type" :options="typeOptions" :disabled="isEditMode" />
                </div>
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isOidc" id="configuration">
            <template #default>Configuration</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <div class="form-group">
                  <CoarTextInput
                    v-model="authority"
                    label="Authority"
                    placeholder="https://accounts.google.com"
                    :required="true"
                  />
                  <p class="field-hint">The OIDC issuer URL (e.g., https://accounts.google.com or https://login.microsoftonline.com/{tenant}/v2.0)</p>
                </div>
                <div class="form-group">
                  <CoarTextInput
                    v-model="clientId"
                    label="Client ID"
                    placeholder="your-client-id"
                    :required="true"
                  />
                </div>
                <div class="form-group">
                  <CoarTextInput
                    v-model="clientSecret"
                    label="Client Secret"
                    placeholder="your-client-secret"
                  />
                  <p class="field-hint">Required for confidential clients. Leave empty for public clients.</p>
                </div>
                <div class="form-group">
                  <CoarTextInput
                    v-model="scopes"
                    label="Scopes"
                    placeholder="openid profile email"
                  />
                  <p class="field-hint">Space-separated list of scopes to request. Default: openid profile email</p>
                </div>
              </CoarCard>
            </template>
          </CoarTab>
        </CoarTabGroup>
      </form>
    </template>
  </div>
</template>

<style scoped>
.form-page { }
.form-group { margin-bottom: 1rem; }
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
.field-hint {
  margin: 0.25rem 0 0;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-tertiary);
}
</style>
