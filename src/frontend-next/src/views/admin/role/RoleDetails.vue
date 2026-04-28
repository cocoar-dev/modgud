<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoleStore } from '@/stores/role.store'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarSelect } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { PERMISSION_RESOURCES, RESOURCE_LABELS } from '@/models/role'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const roleStore = useRoleStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)

const form = ref({
  Name: '',
  Description: '',
  ResourceType: 'todo',
})

const permissions = ref<Set<string>>(new Set())

const resourceTypeOptions = computed(() =>
  Object.keys(PERMISSION_RESOURCES).map(key => ({
    value: key,
    label: RESOURCE_LABELS[key] || key,
  }))
)

const availableActions = computed(() =>
  PERMISSION_RESOURCES[form.value.ResourceType] || []
)

const modalTitle = computed(() => {
  const name = form.value.Name?.trim()
  if (name) return name
  return isCreate.value ? t('admin.roleDetails.createTitle', {}, 'Create Role') : ''
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim() || loading.value,
  onClick: save,
}))

onMounted(async () => {
  if (!isCreate.value) {
    loading.value = true
    try {
      await roleStore.initialize()
      const role = roleStore.roles.find(r => r.Id === props.id)
      if (role) {
        form.value = {
          Name: role.Name,
          Description: role.Description || '',
          ResourceType: role.ResourceType,
        }
        permissions.value = new Set(role.Permissions)
      }
    } finally {
      loading.value = false
    }
  }
})

function togglePermission(action: string) {
  if (permissions.value.has(action)) {
    permissions.value.delete(action)
  } else {
    permissions.value.add(action)
  }
}

function isChecked(action: string): boolean {
  return permissions.value.has(action)
}

function onResourceTypeChange() {
  permissions.value.clear()
}

async function save() {
  if (!form.value.Name.trim()) return
  loading.value = true
  try {
    const dto = {
      Name: form.value.Name,
      Description: form.value.Description || undefined,
      ResourceType: form.value.ResourceType,
      Permissions: [...permissions.value],
    }
    if (isCreate.value) {
      await roleStore.createRole(dto)
    } else {
      await roleStore.updateRole(props.id, dto)
    }
    props.close()
  } catch (e) {
    console.error('Role save failed', e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="shield" :footer-button="footerButton" width="36rem">
    <div v-if="!loading" class="flex flex-col gap-4 p-2 pb-4">
      <!-- Section: Allgemein -->
      <section>
        <div class="section-heading">{{ t('admin.roleDetails.general', {}, 'General') }}</div>
        <div class="flex flex-col gap-2">
          <CoarFormField :label="t('admin.roleDetails.name', {}, 'Name')">
            <CoarTextInput v-model="form.Name" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.roleDetails.description', {}, 'Description')">
            <CoarTextInput v-model="form.Description" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.roleDetails.resourceType', {}, 'Resource Type')">
            <CoarSelect
              v-model="form.ResourceType"
              :options="resourceTypeOptions"
              :disabled="!isCreate"
              @update:model-value="onResourceTypeChange"
            />
          </CoarFormField>
        </div>
      </section>

      <!-- Section: Berechtigungen -->
      <section>
        <div class="section-heading">{{ t('admin.roleDetails.permissions', {}, 'Permissions') }}</div>
        <div class="flex flex-wrap gap-x-4 gap-y-1">
          <CoarCheckbox
            v-for="action in availableActions"
            :key="action"
            :model-value="isChecked(action)"
            :label="action"
            @update:model-value="togglePermission(action)"
          />
        </div>
      </section>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
.section-heading {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
  border-bottom: 1px solid #d1d5db;
  padding-bottom: 4px;
  margin-bottom: 8px;
}
</style>
