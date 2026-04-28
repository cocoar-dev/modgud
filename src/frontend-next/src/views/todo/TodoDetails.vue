<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useTodoStore } from '@/stores/todo.store'
import { useUserStore } from '@/stores/user.store'
import { usePrincipalStore } from '@/stores/principal.store'
import { useCustomerStore } from '@/stores/customer.store'
import { useI18n } from '@cocoar/vue-localization'
import TodoComments from './TodoComments.vue'
import RichTextEditor from '@/components/RichTextEditor.vue'
import ModalLayout from '@/components/ModalLayout.vue'
import type { TodoStatus } from '@/models/todo'
import type { RefPropertyDto } from '@/models/common'

const { t } = useI18n()
import {
  CoarTextInput,
  CoarSelect,
  CoarTagSelect,
  CoarCheckbox,
  CoarFormField,
  CoarTabGroup,
  CoarTab,
  CoarPlainDatePicker,
} from '@cocoar/vue-ui'
import type { CoarSelectOption } from '@cocoar/vue-ui'

// Temporal API
// eslint-disable-next-line @typescript-eslint/no-explicit-any
declare const Temporal: any

const props = defineProps<{
  todoId: string
  customer?: string
  parentTodoId?: string
  tab?: string
  close: (result?: unknown) => void
}>()

const todoStore = useTodoStore()
const userStore = useUserStore()
const principalStore = usePrincipalStore()
const customerStore = useCustomerStore()

const isCreate = computed(() => props.todoId === 'create')
const activeTab = ref<'general' | 'comments'>(props.tab === 'comments' ? 'comments' : 'general')
const loading = ref(false)
const commentsCount = ref(0)

// Form state
const form = ref({
  Title: '',
  Description: '',
  DueDate: '',
  Status: 'None' as TodoStatus,
  Customer: undefined as RefPropertyDto | undefined,
  Responsibles: [] as RefPropertyDto[],
  Critical: false,
  AwaitingFeedback: false,
  ParentTodoId: undefined as string | undefined,
})

// Modal header
const modalTitle = computed(() => {
  if (isCreate.value) {
    return form.value.Title?.trim() || t('todo.details.newTask', {}, 'New Task')
  }
  return form.value.Title || ''
})

const modalSubTitle = computed(() => form.value.Customer?.Label)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Title.trim() || loading.value,
  loading: loading.value,
  onClick: save,
}))

// DueDate conversion: ISO string <-> Temporal.PlainDate
const dueDateValue = computed({
  get: () => form.value.DueDate ? Temporal.PlainDate.from(form.value.DueDate.split('T')[0]) : null,
  set: (val) => { form.value.DueDate = val ? val.toString() : '' },
})

// Select options
const statusSelectOptions = computed<CoarSelectOption<TodoStatus>[]>(() =>
  todoStore.statusOptions.map(o => ({ value: o.value, label: o.label || t('todo.details.noStatus', {}, '(No Status)') }))
)

const customerSelectOptions = computed<CoarSelectOption<string>[]>(() => [
  { value: '', label: t('todo.details.noCustomer', {}, '-- No Customer --') },
  ...customerStore.importantCustomers.map(c => ({ value: c.Id, label: c.Name, group: t('todo.topCustomers', {}, 'Top Customers') })),
  ...customerStore.normalCustomers.map(c => ({ value: c.Id, label: c.Name, group: t('todo.normalCustomers', {}, 'Normal Customers') })),
])

const responsibleSelectOptions = computed<CoarSelectOption<string>[]>(() =>
  principalStore.lookupEntities.map(p => ({
    value: p.Id,
    label: p.Label,
    group: p.Type === 'Group'
      ? t('todo.groupsLabel', {}, 'Groups')
      : t('todo.usersLabel', {}, 'Users'),
  }))
)

// Customer select v-model
const customerValue = computed({
  get: () => form.value.Customer?.Id || '',
  set: (id: string) => {
    if (!id) {
      form.value.Customer = undefined
    } else {
      const c = customerStore.activeCustomers.find(c => c.Id === id)
      form.value.Customer = c ? { Id: c.Id, Label: c.Name } : undefined
    }
  },
})

// Responsibles multi-select v-model — accepts humans AND groups (PrincipalIds).
const responsiblesValue = computed({
  get: () => form.value.Responsibles.map(r => r.Id),
  set: (ids: string[]) => {
    form.value.Responsibles = ids
      .map(id => {
        const p = principalStore.findById(id)
        return p
          ? { Id: p.Id, Label: p.Label, PrincipalType: p.Type } as RefPropertyDto
          : undefined
      })
      .filter(Boolean) as RefPropertyDto[]
  },
})

onMounted(async () => {
  // Ensure stores are loaded for select options
  await Promise.all([
    principalStore.loadLookup(),
    customerStore.initialize(),
  ])

  if (!isCreate.value) {
    loading.value = true
    try {
      const details = await todoStore.getDetailsModel(props.todoId)
      form.value = {
        Title: details.Title,
        Description: details.Description || '',
        DueDate: details.DueDate || '',
        Status: details.Status,
        Customer: details.Customer,
        Responsibles: details.Responsibles || [],
        Critical: details.Critical,
        AwaitingFeedback: details.AwaitingFeedback,
        ParentTodoId: details.ParentTodoId,
      }
      commentsCount.value = (details as any).CommentsCount || 0
    } catch (e) {
      console.error('Failed to load todo', e)
    } finally {
      loading.value = false
    }
  } else {
    const customerName = props.customer
    if (customerName) {
      const c = customerStore.getCustomerByName(customerName)
      if (c) form.value.Customer = { Id: c.Id, Label: c.Name }
    }
    if (props.parentTodoId) {
      form.value.ParentTodoId = props.parentTodoId
    }
  }
})

function updateTodoInStore(patch: Partial<{ CommentsCount: number; UnreadComments: number }>) {
  const existing = todoStore.getFromStore(props.todoId)
  if (existing) {
    todoStore.setStoreEntities([{ ...existing, ...patch }])
  }
}

function onCommentsCountChanged(count: number) {
  commentsCount.value = count
  updateTodoInStore({ CommentsCount: count })
}

function onUnreadChanged(delta: number) {
  const existing = todoStore.getFromStore(props.todoId)
  if (existing) {
    const newUnread = Math.max(0, (existing.UnreadComments ?? 0) + delta)
    updateTodoInStore({ UnreadComments: newUnread })
  }
}

async function save() {
  if (!form.value.Title.trim()) return
  loading.value = true
  try {
    if (isCreate.value) {
      await todoStore.createNew({
        Title: form.value.Title,
        Description: form.value.Description || undefined,
        DueDate: form.value.DueDate || undefined,
        Status: form.value.Status,
        Customer: form.value.Customer,
        Responsibles: form.value.Responsibles.length > 0 ? form.value.Responsibles : undefined,
        Critical: form.value.Critical,
        AwaitingFeedback: form.value.AwaitingFeedback,
      }, form.value.ParentTodoId)
    } else {
      await todoStore.updateTodo(props.todoId, {
        Title: form.value.Title,
        Description: form.value.Description || undefined,
        DueDate: form.value.DueDate || undefined,
        Status: form.value.Status,
        Customer: form.value.Customer,
        Responsibles: form.value.Responsibles,
        Critical: form.value.Critical,
        AwaitingFeedback: form.value.AwaitingFeedback,
      })
    }
    props.close()
  } catch (e) {
    console.error('Save failed', e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout
    :close="close"
    :title="modalTitle"
    :sub-title="modalSubTitle"
    icon="clipboard-check"
    :footer-button="footerButton"
    width="64rem"
  >
    <div class="flex flex-col flex-1 min-h-0">
      <!-- Tabs -->
      <div v-if="!isCreate">
        <CoarTabGroup v-model="activeTab">
          <CoarTab id="general">{{ t('todo.details.general', {}, 'General') }}</CoarTab>
          <CoarTab id="comments">{{ t('todo.details.comments', { count: commentsCount }, 'Comments ({count})') }}</CoarTab>
        </CoarTabGroup>
      </div>

      <!-- Content -->
      <div class="flex-1 flex flex-col overflow-hidden pt-6 min-h-0" v-if="!loading">
        <!-- General Tab -->
        <div v-show="activeTab === 'general'" class="flex gap-6 flex-1 min-h-0">
          <!-- Main form area (left) -->
          <div class="flex-1 flex flex-col min-h-0">
            <div class="flex items-end gap-4 flex-shrink-0">
              <CoarFormField :label="t('todo.details.title', {}, 'Title')" class="flex-1">
                <CoarTextInput v-model="form.Title" clearable />
              </CoarFormField>
              <div class="flex items-center pb-2">
                <CoarCheckbox v-model="form.Critical" :label="t('common.important', {}, 'Important')" />
              </div>
            </div>

            <CoarFormField :label="t('todo.details.description', {}, 'Description')" class="flex! flex-1 flex-col min-h-0 mt-4">
              <RichTextEditor v-model="form.Description" height="100%" class="flex-1" />
            </CoarFormField>
          </div>

          <!-- Sidebar (right) -->
          <div class="w-56 space-y-4 flex-shrink-0 overflow-y-auto">
            <CoarFormField :label="t('common.status', {}, 'Status')">
              <CoarSelect v-model="form.Status" :options="statusSelectOptions" />
            </CoarFormField>

            <CoarFormField :label="t('todo.customer', {}, 'Customer')">
              <CoarSelect
                v-model="customerValue"
                :options="customerSelectOptions"
                :disabled="!!form.ParentTodoId"
                searchable
                sort-groups="desc"
                sort-options="asc"
              />
            </CoarFormField>

            <CoarFormField :label="t('todo.details.dueDate', {}, 'Due Date')">
              <CoarPlainDatePicker v-model="dueDateValue" />
            </CoarFormField>

            <CoarFormField :label="t('todo.responsible', {}, 'Responsible')">
              <CoarTagSelect
                v-model="responsiblesValue"
                :options="responsibleSelectOptions"
              />
            </CoarFormField>

            <CoarCheckbox v-model="form.AwaitingFeedback" :label="t('todo.details.awaitingFeedback', {}, 'Awaiting Feedback')" />
          </div>
        </div>

        <!-- Comments Tab -->
        <div v-if="activeTab === 'comments' && !isCreate" class="flex-1 flex flex-col min-h-0">
          <TodoComments
            :todo-id="todoId"
            @count-changed="onCommentsCountChanged"
            @unread-changed="onUnreadChanged"
          />
        </div>
      </div>

      <div v-else class="flex-1 flex items-center justify-center p-8">
        <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
      </div>
    </div>
  </ModalLayout>
</template>
