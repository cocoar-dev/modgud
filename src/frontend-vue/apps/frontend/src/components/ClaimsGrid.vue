<script setup lang="ts">
import { CoarTextInput, CoarButton } from '@cocoar/vue-ui';

export interface Claim {
  type: string;
  value: string;
}

const props = defineProps<{
  modelValue: Claim[];
}>();

const emit = defineEmits<{
  'update:modelValue': [value: Claim[]];
}>();

function addRow() {
  emit('update:modelValue', [...props.modelValue, { type: '', value: '' }]);
}

function removeRow(index: number) {
  const updated = [...props.modelValue];
  updated.splice(index, 1);
  emit('update:modelValue', updated);
}

function updateType(index: number, newType: string) {
  const updated = [...props.modelValue];
  updated[index] = { ...updated[index], type: newType };
  emit('update:modelValue', updated);
}

function updateValue(index: number, newValue: string) {
  const updated = [...props.modelValue];
  updated[index] = { ...updated[index], value: newValue };
  emit('update:modelValue', updated);
}
</script>

<template>
  <div class="claims-grid">
    <table class="claims-table">
      <thead>
        <tr>
          <th class="claims-th">Type</th>
          <th class="claims-th">Value</th>
          <th class="claims-th claims-th-action"></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(claim, index) in modelValue" :key="index" class="claims-row">
          <td class="claims-td">
            <CoarTextInput
              :model-value="claim.type"
              placeholder="Claim type"
              @update:model-value="updateType(index, $event)"
            />
          </td>
          <td class="claims-td">
            <CoarTextInput
              :model-value="claim.value"
              placeholder="Claim value"
              @update:model-value="updateValue(index, $event)"
            />
          </td>
          <td class="claims-td claims-td-action">
            <CoarButton variant="ghost" size="s" @click="removeRow(index)">Delete</CoarButton>
          </td>
        </tr>
        <tr v-if="modelValue.length === 0">
          <td colspan="3" class="claims-empty">No claims defined.</td>
        </tr>
      </tbody>
    </table>
    <div class="claims-add">
      <CoarButton variant="secondary" size="s" @click="addRow">Add Claim</CoarButton>
    </div>
  </div>
</template>

<style scoped>
.claims-grid {
  width: 100%;
}

.claims-table {
  width: 100%;
  border-collapse: collapse;
}

.claims-th {
  text-align: left;
  padding: 0.5rem 0.5rem 0.5rem 0;
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--coar-text-neutral-secondary);
  border-bottom: 1px solid var(--coar-border-neutral-secondary);
}

.claims-th-action {
  width: 4rem;
}

.claims-td {
  padding: 0.375rem 0.5rem 0.375rem 0;
  vertical-align: middle;
  border-bottom: 1px solid var(--coar-border-neutral-tertiary);
}

.claims-td-action {
  width: 4rem;
  text-align: right;
  padding-right: 0;
}

.claims-row:last-child .claims-td {
  border-bottom: none;
}

.claims-empty {
  padding: 1.5rem;
  text-align: center;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary);
}

.claims-add {
  margin-top: 0.75rem;
}
</style>
