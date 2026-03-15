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
const configuration = ref('');

const activeTab = ref<'basic' | 'configuration'>('basic');

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

const typeOptions = [
  { value: 'Internal', label: 'Internal' },
  { value: 'OpenIdConnect', label: 'OpenID Connect' },
];

watch([name, displayName, description, type, configuration], () => {
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
    configuration.value = provider.configuration || '';
  } catch {
    error.value = 'Failed to load login provider.';
  } finally {
    isLoading.value = false;
    setTimeout(() => { isDirty.value = false; }, 0);
  }
});

async function onSubmit() {
  if (!name.value) return;
  isSaving.value = true;
  error.value = '';
  try {
    if (isEditMode.value) {
      await adminApi.updateLoginProvider(id.value!, {
        displayName: displayName.value || undefined,
        description: description.value || null,
        configuration: type.value === 'OpenIdConnect' ? (configuration.value || null) : null,
      });
    } else {
      await adminApi.createLoginProvider({
        name: name.value,
        displayName: displayName.value || undefined,
        description: description.value || undefined,
        type: type.value,
        configuration: type.value === 'OpenIdConnect' ? (configuration.value || undefined) : undefined,
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

          <CoarTab v-if="(type as string) === 'OpenIdConnect'" id="configuration">
            <template #default>Configuration</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <div class="form-group">
                  <CoarTextInput v-model="configuration" label="Configuration (JSON)" :rows="12" />
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

</style>
