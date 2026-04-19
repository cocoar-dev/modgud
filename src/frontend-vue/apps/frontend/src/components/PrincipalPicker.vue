<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarTextInput, CoarIcon, CoarTag, CoarSpinner } from '@cocoar/vue-ui'
import { usePrincipalStore, type PrincipalLookupDto } from '@/stores/principal.store'

const props = withDefaults(defineProps<{
  modelValue: string[]
  typeFilter?: 'Person' | 'Group'
  excludeIds?: string[]
  placeholder?: string
  disabled?: boolean
}>(), {
  placeholder: 'Search people and groups...',
  excludeIds: () => [],
  disabled: false,
})

const emit = defineEmits<{
  (e: 'update:modelValue', value: string[]): void
}>()

const principalStore = usePrincipalStore()

const query = ref('')
const results = ref<PrincipalLookupDto[]>([])
const open = ref(false)
const loading = ref(false)
let searchHandle: ReturnType<typeof setTimeout> | null = null

const selectedIds = computed(() => props.modelValue)

const selectedPrincipals = computed<PrincipalLookupDto[]>(() => {
  return selectedIds.value
    .map(id => principalStore.findInCache(id))
    .filter((p): p is PrincipalLookupDto => !!p)
})

async function doSearch(q: string) {
  loading.value = true
  try {
    const list = await principalStore.search(q || undefined, props.typeFilter)
    // Filter out already-selected + excluded IDs
    results.value = list.filter(p =>
      !selectedIds.value.includes(p.Id) && !props.excludeIds.includes(p.Id),
    )
  } catch {
    results.value = []
  } finally {
    loading.value = false
  }
}

watch(query, (q) => {
  if (searchHandle) clearTimeout(searchHandle)
  searchHandle = setTimeout(() => doSearch(q), 200)
})

onMounted(async () => {
  // Warm the cache so already-selected chips can render with proper labels
  for (const id of selectedIds.value) {
    if (!principalStore.findInCache(id)) {
      await principalStore.getById(id)
    }
  }
})

function add(p: PrincipalLookupDto) {
  if (selectedIds.value.includes(p.Id)) return
  emit('update:modelValue', [...selectedIds.value, p.Id])
  query.value = ''
  results.value = results.value.filter(r => r.Id !== p.Id)
}

function remove(id: string) {
  emit('update:modelValue', selectedIds.value.filter(x => x !== id))
}

function iconFor(p: PrincipalLookupDto): string {
  return p.Type === 'Group' ? 'users' : 'user'
}

function onFocus() {
  open.value = true
  if (results.value.length === 0) doSearch(query.value)
}

function onBlur() {
  // Delay to let clicks on results register
  setTimeout(() => { open.value = false }, 150)
}
</script>

<template>
  <div class="picker" :class="{ 'is-disabled': disabled }">
    <div v-if="selectedPrincipals.length > 0" class="chips">
      <CoarTag
        v-for="p in selectedPrincipals"
        :key="p.Id"
        size="s"
        variant="neutral"
        :removable="!disabled"
        @remove="remove(p.Id)"
      >
        <template #icon>
          <CoarIcon :name="iconFor(p)" size="s" />
        </template>
        {{ p.DisplayLabel }}
      </CoarTag>
    </div>

    <div class="search-wrap">
      <CoarTextInput
        v-model="query"
        :placeholder="placeholder"
        :disabled="disabled"
        size="s"
        @focus="onFocus"
        @blur="onBlur"
      />
      <div v-if="open && !disabled" class="dropdown">
        <div v-if="loading" class="hint">
          <CoarSpinner size="xs" /> Searching...
        </div>
        <div v-else-if="results.length === 0" class="hint">
          <span v-if="query">No matches.</span>
          <span v-else>Start typing to search.</span>
        </div>
        <button
          v-for="p in results"
          :key="p.Id"
          type="button"
          class="result"
          @mousedown.prevent="add(p)"
        >
          <CoarIcon :name="iconFor(p)" size="s" />
          <div class="result-label">
            <div class="result-title">{{ p.DisplayLabel }}</div>
            <div v-if="p.Email" class="result-sub">{{ p.Email }}</div>
          </div>
          <span class="result-type">{{ p.Type }}</span>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.picker {
  display: flex;
  flex-direction: column;
  gap: 6px;
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

.search-wrap {
  position: relative;
}

.dropdown {
  position: absolute;
  left: 0;
  right: 0;
  top: calc(100% + 4px);
  background: var(--coar-background-neutral-primary, white);
  border: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  border-radius: var(--coar-radius-m, 4px);
  box-shadow: 0 10px 24px -4px rgba(0, 0, 0, 0.12);
  max-height: 240px;
  overflow-y: auto;
  z-index: 50;
}

.hint {
  padding: 10px 12px;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary, #64748b);
  display: flex;
  align-items: center;
  gap: 6px;
}

.result {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  background: transparent;
  border: none;
  cursor: pointer;
  text-align: left;
  color: inherit;
}

.result:hover {
  background: var(--coar-background-neutral-tertiary, #f1f5f9);
}

.result-label {
  flex: 1;
  min-width: 0;
}

.result-title {
  font-size: 0.875rem;
  font-weight: 500;
  color: var(--coar-text-neutral-primary, #0f172a);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.result-sub {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #64748b);
}

.result-type {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary, #64748b);
}
</style>
