<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { CoarIcon, CoarTag, CoarSelect } from '@cocoar/vue-ui'
import { usePermissionRoleStore } from '@/stores/permission-role.store'

const props = defineProps<{
  modelValue: string[]
  disabled?: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: string[]): void
}>()

const roleStore = usePermissionRoleStore()

const selectedIds = computed(() => props.modelValue)

const availableRoles = computed(() =>
  roleStore.entities.filter(r => !selectedIds.value.includes(r.Id)),
)

const selectedRoles = computed(() =>
  selectedIds.value
    .map(id => roleStore.entities.find(r => r.Id === id))
    .filter((r): r is NonNullable<typeof r> => !!r),
)

const selectOptions = computed(() =>
  availableRoles.value.map(r => ({
    value: r.Id,
    label: `${r.Name}${r.ResourceType ? ` (${r.ResourceType})` : ''}`,
  })),
)

function addRole(id: string | null | undefined) {
  if (!id) return
  if (selectedIds.value.includes(id)) return
  emit('update:modelValue', [...selectedIds.value, id])
}

function remove(id: string) {
  emit('update:modelValue', selectedIds.value.filter(x => x !== id))
}

onMounted(() => {
  if (roleStore.entities.length === 0) {
    roleStore.initialize()
    roleStore.loadAll()
  }
})
</script>

<template>
  <div class="picker" :class="{ 'is-disabled': disabled }">
    <div v-if="selectedRoles.length > 0" class="chips">
      <CoarTag
        v-for="r in selectedRoles"
        :key="r.Id"
        size="s"
        variant="neutral"
        :removable="!disabled"
        @remove="remove(r.Id)"
      >
        <template #icon>
          <CoarIcon name="shield" size="s" />
        </template>
        {{ r.Name }}
        <span v-if="r.ResourceType" class="resource-hint">· {{ r.ResourceType }}</span>
      </CoarTag>
    </div>

    <CoarSelect
      :model-value="null"
      :options="selectOptions"
      placeholder="Add role..."
      size="s"
      :disabled="disabled || selectOptions.length === 0"
      @update:model-value="addRole"
    />
  </div>
</template>

<style scoped>
.picker {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.picker.is-disabled {
  opacity: 0.6;
  pointer-events: none;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.resource-hint {
  opacity: 0.6;
  margin-left: 4px;
  font-size: 0.7rem;
}
</style>
