<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { CoarCard, CoarNote, CoarTextInput, CoarSpinner, useToast } from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();

const slug = computed(() => route.params.slug as string | undefined);
const isEditMode = computed(() => !!slug.value);

// Form fields
const slugField = ref('');
const displayName = ref('');
const description = ref('');

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

watch([slugField, displayName, description], () => { isDirty.value = true; });

ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit Realm' : 'Create Realm';
  ctx.header.subTitle = isEditMode.value ? 'Update realm details' : 'Create a new realm';
  ctx.content.scrollable = true;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/realms');
  ctx.footer.button2.visible = isEditMode.value;
  ctx.footer.button2.text = 'Delete';
  ctx.footer.button2.onClick = () => onDelete();
  ctx.footer.button3.visible = true;
  ctx.footer.button3.text = isEditMode.value ? 'Save Changes' : 'Create';
  ctx.footer.button3.onClick = () => onSubmit();
});

watch(isSaving, (val) => { ui.state.footer.button3.loading = val; });

const toast = useToast();

async function onDelete() {
  if (!confirm('Are you sure you want to delete this realm?')) return;
  try {
    await adminApi.deleteRealm(slug.value!);
    isDirty.value = false;
    toast.success('Realm deleted.');
    router.push('/admin/realms');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to delete realm.';
  }
}

onMounted(async () => {
  if (!isEditMode.value) return;
  isLoading.value = true;
  try {
    const realm = await adminApi.getRealm(slug.value!);
    slugField.value = realm.slug;
    displayName.value = realm.displayName;
    description.value = realm.description || '';
  } catch {
    error.value = 'Failed to load realm.';
  } finally {
    isLoading.value = false;
    setTimeout(() => { isDirty.value = false; }, 0);
  }
});

async function onSubmit() {
  if (!displayName.value) return;
  if (!isEditMode.value && !slugField.value) return;

  isSaving.value = true;
  error.value = '';
  try {
    if (isEditMode.value) {
      await adminApi.updateRealm(slug.value!, {
        displayName: displayName.value,
        description: description.value || null,
      });
    } else {
      await adminApi.createRealm({
        slug: slugField.value,
        displayName: displayName.value,
        description: description.value || undefined,
      });
    }
    isDirty.value = false;
    router.push('/admin/realms');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save realm.';
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
        <CoarCard padding="l" class="form-card">
          <div class="form-group">
            <CoarTextInput
              v-model="slugField"
              label="Slug"
              placeholder="e.g. my-realm"
              :required="true"
              :disabled="isEditMode"
            />
          </div>
          <div class="form-group">
            <CoarTextInput
              v-model="displayName"
              label="Display Name"
              placeholder="Enter display name"
              :required="true"
            />
          </div>
          <div class="form-group">
            <CoarTextInput
              v-model="description"
              label="Description"
              placeholder="Enter realm description"
              :rows="3"
            />
          </div>
        </CoarCard>
      </form>
    </template>
  </div>
</template>

<style scoped>
.form-page { }
.form-group { margin-bottom: 1rem; }
.form-group:last-child { margin-bottom: 0; }
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
</style>
