<script setup lang="ts">
import { ref, computed } from 'vue';
import { CoarTextInput } from '@cocoar/vue-ui';

export interface DualListItem {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  icon?: string;
}

const props = withDefaults(defineProps<{
  modelValue: string[];
  items: DualListItem[];
  assignedLabel?: string;
  availableLabel?: string;
  filterPlaceholder?: string;
}>(), {
  assignedLabel: 'Assigned',
  availableLabel: 'Available',
  filterPlaceholder: 'Filter...',
});

const emit = defineEmits<{
  'update:modelValue': [value: string[]];
}>();

const assignedFilter = ref('');
const availableFilter = ref('');

const assignedItems = computed(() => {
  const ids = new Set(props.modelValue);
  let result = props.items.filter((item) => ids.has(item.id));
  if (assignedFilter.value) {
    const q = assignedFilter.value.toLowerCase();
    result = result.filter((item) => matchesFilter(item, q));
  }
  return result;
});

const availableItems = computed(() => {
  const ids = new Set(props.modelValue);
  let result = props.items.filter((item) => !ids.has(item.id));
  if (availableFilter.value) {
    const q = availableFilter.value.toLowerCase();
    result = result.filter((item) => matchesFilter(item, q));
  }
  return result;
});

function matchesFilter(item: DualListItem, query: string): boolean {
  return (
    item.name.toLowerCase().includes(query) ||
    (item.displayName?.toLowerCase().includes(query) ?? false) ||
    (item.description?.toLowerCase().includes(query) ?? false)
  );
}

function assign(item: DualListItem) {
  emit('update:modelValue', [...props.modelValue, item.id]);
}

function unassign(item: DualListItem) {
  emit('update:modelValue', props.modelValue.filter((id) => id !== item.id));
}
</script>

<template>
  <div class="dual-list">
    <!-- Assigned panel -->
    <div class="dual-list-panel">
      <div class="dual-list-header">{{ assignedLabel }} ({{ assignedItems.length }})</div>
      <div class="dual-list-filter">
        <CoarTextInput v-model="assignedFilter" :placeholder="filterPlaceholder" />
      </div>
      <div class="dual-list-items">
        <div
          v-for="item in assignedItems"
          :key="item.id"
          class="dual-list-item"
          @click="unassign(item)"
        >
          <span v-if="item.icon" class="dual-list-item-icon">{{ item.icon }}</span>
          <div class="dual-list-item-content">
            <span class="dual-list-item-name">{{ item.name }}</span>
            <span v-if="item.displayName" class="dual-list-item-display">{{ item.displayName }}</span>
            <span v-if="item.description" class="dual-list-item-desc">{{ item.description }}</span>
          </div>
        </div>
        <div v-if="assignedItems.length === 0" class="dual-list-empty">No items</div>
      </div>
    </div>

    <!-- Available panel -->
    <div class="dual-list-panel">
      <div class="dual-list-header">{{ availableLabel }} ({{ availableItems.length }})</div>
      <div class="dual-list-filter">
        <CoarTextInput v-model="availableFilter" :placeholder="filterPlaceholder" />
      </div>
      <div class="dual-list-items">
        <div
          v-for="item in availableItems"
          :key="item.id"
          class="dual-list-item"
          @click="assign(item)"
        >
          <span v-if="item.icon" class="dual-list-item-icon">{{ item.icon }}</span>
          <div class="dual-list-item-content">
            <span class="dual-list-item-name">{{ item.name }}</span>
            <span v-if="item.displayName" class="dual-list-item-display">{{ item.displayName }}</span>
            <span v-if="item.description" class="dual-list-item-desc">{{ item.description }}</span>
          </div>
        </div>
        <div v-if="availableItems.length === 0" class="dual-list-empty">No items</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.dual-list {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.75rem;
  min-height: 250px;
}

.dual-list-panel {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: var(--coar-radius-m);
  background: var(--coar-background-neutral-primary);
  overflow: hidden;
}

.dual-list-header {
  padding: 0.625rem 0.75rem;
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--coar-text-neutral-secondary);
  border-bottom: 1px solid var(--coar-border-neutral-tertiary);
  text-transform: uppercase;
  letter-spacing: 0.03em;
}

.dual-list-filter {
  padding: 0.5rem 0.75rem;
  border-bottom: 1px solid var(--coar-border-neutral-tertiary);
}

.dual-list-items {
  flex: 1;
  overflow-y: auto;
  min-height: 0;
  max-height: 320px;
}

.dual-list-item {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  padding: 0.5rem 0.75rem;
  cursor: pointer;
  transition: background-color 0.15s ease;
  border-bottom: 1px solid var(--coar-border-neutral-tertiary);
}

.dual-list-item:last-child {
  border-bottom: none;
}

.dual-list-item:hover {
  background: var(--coar-background-neutral-secondary);
}

.dual-list-item-icon {
  flex-shrink: 0;
  font-size: 1rem;
  line-height: 1.4;
}

.dual-list-item-content {
  display: flex;
  flex-direction: column;
  gap: 0.0625rem;
  min-width: 0;
}

.dual-list-item-name {
  font-size: 0.875rem;
  font-weight: 600;
  color: var(--coar-text-neutral-primary);
}

.dual-list-item-display {
  font-size: 0.8125rem;
  font-style: italic;
  color: var(--coar-text-neutral-secondary);
}

.dual-list-item-desc {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary);
  line-height: 1.35;
}

.dual-list-empty {
  padding: 1.5rem;
  text-align: center;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary);
}
</style>
