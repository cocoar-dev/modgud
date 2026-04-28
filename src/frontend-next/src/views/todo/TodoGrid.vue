<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useFragmentNavigation } from '@cocoar/vue-fragment-parser'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  CoarCheckbox,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
  CoarMenu,
  CoarMenuDivider,
  CoarSubFlyout,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useTodoStore } from '@/stores/todo.store'
import {
  criticalColumn, awaitingFeedbackColumn, titleColumn, descriptionColumn,
  statusColumn, commentsColumn, dueDateColumn, responsiblesColumn,
  customerColumn, createdByColumn, lastModifiedColumn,
} from './composables/useTodoGridColumns'
import HtmlTooltip from './HtmlTooltip.vue'
import type { TodoListDto, TodoStatus } from '@/models/todo'

const { t } = useI18n()

interface TodoTreeNode extends TodoListDto {
  children: TodoTreeNode[]
  isPhantomParent?: boolean
}

const props = defineProps<{
  customerFilter?: string
  archiveView?: boolean
}>()

const { navigateToModal } = useFragmentNavigation()
const todoStore = useTodoStore()
const { openRows } = storeToRefs(todoStore)

// Context menu
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const contextTodo = ref<TodoTreeNode | undefined>()
const selectedIds = ref<string[]>([])

// Search & filters
const searchText = ref('')
const showSubTodos = ref(false)
const moveMode = ref(false)

// Archive support
const archivedTodos = ref<TodoListDto[]>([])

watch(() => props.archiveView, async (isArchive) => {
  if (isArchive) {
    const dtos = await todoStore.getArchived()
    archivedTodos.value = dtos.map(d => ({
      Id: d.Id, Title: d.Title, Description: d.Description, DueDate: d.DueDate,
      Status: d.Status, Customer: d.Customer, Responsibles: d.Responsibles,
      Critical: d.Critical, AwaitingFeedback: d.AwaitingFeedback,
      CommentsCount: d.CommentsCount, UnreadComments: d.UnreadComments,
      CreatedBy: d.CreatedBy, LastTouchedAt: d.LastTouchedAt,
      ParentTodoId: d.ParentTodoId, ChildTodosCount: d.ChildTodosCount,
      ChildTodosUnreadCommentsCount: d.ChildTodosUnreadCommentsCount,
      IsArchived: d.IsArchived, AggregateVersion: d.AggregateVersion,
      EntityStatus: d.EntityStatus,
    }))
  } else {
    archivedTodos.value = []
  }
}, { immediate: true })

// Transform flat todos → nested tree
const treeData = computed<TodoTreeNode[]>(() => {
  let todos = props.archiveView ? archivedTodos.value : todoStore.todoslist
  if (props.customerFilter) {
    todos = todos.filter(t => t.Customer?.Label === props.customerFilter)
  }

  const allIds = new Set(todos.map(t => t.Id))
  const childrenMap = new Map<string, TodoTreeNode[]>()
  const orphanMap = new Map<string, TodoTreeNode[]>()
  const parents: TodoTreeNode[] = []

  for (const t of todos) {
    const node: TodoTreeNode = { ...t, children: [] }
    if (t.ParentTodoId) {
      if (allIds.has(t.ParentTodoId)) {
        const siblings = childrenMap.get(t.ParentTodoId) || []
        siblings.push(node)
        childrenMap.set(t.ParentTodoId, siblings)
      } else {
        // Orphan: parent not in scope
        const siblings = orphanMap.get(t.ParentTodoId) || []
        siblings.push(node)
        orphanMap.set(t.ParentTodoId, siblings)
      }
    } else {
      parents.push(node)
    }
  }

  for (const p of parents) {
    const children = childrenMap.get(p.Id) || []
    children.sort((a, b) => {
      const ad = a.LastTouchedAt ? new Date(a.LastTouchedAt).getTime() : 0
      const bd = b.LastTouchedAt ? new Date(b.LastTouchedAt).getTime() : 0
      return bd - ad
    })
    p.children = children
  }

  // Create phantom parents for orphan subtodos (parent not visible due to access control)
  for (const [parentId, orphans] of orphanMap) {
    orphans.sort((a, b) => {
      const ad = a.LastTouchedAt ? new Date(a.LastTouchedAt).getTime() : 0
      const bd = b.LastTouchedAt ? new Date(b.LastTouchedAt).getTime() : 0
      return bd - ad
    })
    const phantom: TodoTreeNode = {
      Id: `phantom-${parentId}`,
      Title: t('todo.phantomParent', {}, 'Task not visible'),
      Status: 'None',
      Responsibles: [],
      Critical: false,
      AwaitingFeedback: false,
      CommentsCount: 0,
      UnreadComments: 0,
      ChildTodosCount: orphans.length,
      ChildTodosUnreadCommentsCount: 0,
      IsArchived: false,
      AggregateVersion: 0,
      EntityStatus: 'Active',
      children: orphans,
      isPhantomParent: true,
    }
    parents.push(phantom)
  }

  // Sort parents by LastTouchedAt desc (sorting must happen here, not via AG Grid,
  // because AG Grid sort would break the tree parent-child ordering)
  parents.sort((a, b) => {
    const ad = a.LastTouchedAt ? new Date(a.LastTouchedAt).getTime() : 0
    const bd = b.LastTouchedAt ? new Date(b.LastTouchedAt).getTime() : 0
    return bd - ad
  })

  return parents
})

/** Check if a row matches the search query (same fields as AG Grid quickFilter columns) */
function matchesRow(row: TodoTreeNode, query: string): boolean {
  const searchable = [
    row.Title,
    row.Description?.replace(/<[^>]*>/g, ''),
    row.Responsibles?.map(r => r.Label).join(' '),
    row.Customer?.Label,
    row.CreatedBy?.Label,
  ].filter(Boolean).join(' ').toLowerCase()
  return searchable.includes(query)
}

// Writable ref for builder binding (computed is readonly)
const treeDataRef = ref<TodoTreeNode[]>([])
watch(treeData, (val) => { treeDataRef.value = val }, { immediate: true })

// Phantom parent IDs must always be in openRows — writable computed guarantees this on every get/set
const phantomIds = computed(() => treeData.value.filter(n => n.isPhantomParent).map(n => n.Id))
const guardedOpenRows = computed({
  get: () => {
    const ids = openRows.value
    const missing = phantomIds.value.filter(id => !ids.includes(id))
    return missing.length > 0 ? [...ids, ...missing] : ids
  },
  set: (val: string[]) => {
    const phantoms = phantomIds.value
    if (phantoms.length === 0) {
      openRows.value = val
      return
    }
    const missing = phantoms.filter(id => !val.includes(id))
    openRows.value = missing.length > 0 ? [...val, ...missing] : val
  },
})

// Status options for context menu
const statusOptions = todoStore.statusOptions.filter(o => o.value !== 'None')

// Grid builder
const builder = CoarGridBuilder.create<TodoTreeNode>()
  .persistColumnState('todo')
  .treeData({
    children: (row) => row.children,
    rowId: (row) => row.Id,
  })
  .openRows(guardedOpenRows)
  .rowDataRef(treeDataRef)
  .quickFilterText(searchText)
  .customFilter((todos, search) => {
    // null → fall back to default quickFilter (per-row matching)
    if (!showSubTodos.value) return null

    if (!search.trim()) return todos
    const q = search.toLowerCase()

    // Keep entire parent group when any member matches
    return todos.filter(parent =>
      matchesRow(parent, q) ||
      parent.children.some(child => matchesRow(child, q))
    )
  })
  .forceExpanded(showSubTodos)
  .updateOn(showSubTodos)
  .searchHighlight()
  .rowSelection('multiple')
  .rowClassRules({
    'status-inProgress': (p) => p.data?.Status === 'InProgress',
    'status-none': (p) => p.data?.Status === 'None',
    'status-pending': (p) => p.data?.EntityStatus === 'Pending',
    'infoTodo': (p) => p.data?.Status === 'Info',
    'childTodo': (p) => !!p.data?.ParentTodoId,
    'parentTodo': (p) => !p.data?.ParentTodoId && !p.data?.isPhantomParent,
    'phantomParent': (p) => !!p.data?.isPhantomParent,
  })
  .rowDragHighlight({
    canDrop: (_dragged, target) => !target.ParentTodoId && !target.isPhantomParent,
  })
  .option('suppressRowDrag', true)
  .onRowDragEnd((event) => {
    const dragged = event.node.data
    const target = event.overNode?.data
    if (dragged && target && dragged.Id !== target.Id) {
      todoStore.convertToSubTodo(dragged.Id, target.Id)
    }
  })
  .onCellDoubleClicked((event) => {
    if (event.data && !event.data.isPhantomParent) {
      const colId = event.column?.getColId() ?? event.colDef?.colId
      const params = colId === 'CommentsCount' ? { tab: 'comments' } : undefined
      navigateToModal(event.data.Id, params)
    }
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .onCellContextMenu((event) => {
    if (event.data?.isPhantomParent) return
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    contextTodo.value = event.data
    selectedIds.value = event.api.getSelectedRows().map((r: TodoTreeNode) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    criticalColumn(),
    awaitingFeedbackColumn(),
    titleColumn({ rowDrag: true, sortable: true, dueDateWarning: true }),
    descriptionColumn(HtmlTooltip),
    statusColumn(),
    commentsColumn({ quickFilter: false }),
    dueDateColumn({ warnDays: 3 }),
    responsiblesColumn({ quickFilter: true }),
    customerColumn({ quickFilter: true }),
    createdByColumn(),
    lastModifiedColumn(),
  ])

// Context menu actions
async function setStatus(status: TodoStatus) {
  if (selectedIds.value.length === 0) return
  await todoStore.updateStatus({ Ids: selectedIds.value, Status: status })
}

async function setCritical(value: boolean) {
  if (selectedIds.value.length === 0) return
  await todoStore.patchFlags({
    Ids: selectedIds.value,
    AddFlags: value ? ['Critical'] : undefined,
    RemoveFlags: value ? undefined : ['Critical'],
  })
}

async function setAwaitingFeedback(value: boolean) {
  if (selectedIds.value.length === 0) return
  await todoStore.patchFlags({
    Ids: selectedIds.value,
    AddFlags: value ? ['AwaitingFeedback'] : undefined,
    RemoveFlags: value ? undefined : ['AwaitingFeedback'],
  })
}

async function archiveTodos() {
  if (selectedIds.value.length > 0) {
    await todoStore.archive(selectedIds.value)
  }
}

async function restoreTodos() {
  if (selectedIds.value.length > 0) {
    await todoStore.restore(selectedIds.value)
  }
}

function createSubTodo() {
  const todo = contextTodo.value
  if (todo && !todo.ParentTodoId) {
    navigateToModal('create', { parentTodoId: todo.Id, customer: todo.Customer?.Label })
  }
}

async function deleteTodos() {
  if (selectedIds.value.length > 0 && confirm(t('common.confirmDelete', {}, 'Really delete?'))) {
    await todoStore.deleteTodos(selectedIds.value)
  }
}

function toggleMoveMode() {
  moveMode.value = !moveMode.value
  builder.api?.setGridOption('suppressRowDrag', !moveMode.value)
}

async function convertToParentTodo() {
  const todo = contextTodo.value
  if (todo?.ParentTodoId) {
    await todoStore.convertToParentTodo([todo.Id])
  }
}

function resetColumnState() {
  builder.resetPersistedState()
}
</script>

<template>
  <CoarDataGrid
    :builder="builder"
    show-search
    class="h-full"
    bordered
    elevated
  >
    <template #toolbar-right>
      <CoarCheckbox v-model="showSubTodos" :label="t('todo.showSubTasks', {}, 'show all subtasks')" />
      <CoarButton size="s" icon-start="plus" @click="navigateToModal('create', customerFilter ? { customer: customerFilter } : undefined)">{{ t('common.create', {}, 'Create') }}</CoarButton>
    </template>
  </CoarDataGrid>

  <!-- Row context menu -->
  <CoarContextMenu :menu="cellMenu">
    <CoarMenuItem
      v-if="contextTodo && !contextTodo.ParentTodoId"
      :label="t('common.create', {}, 'Create')"
      icon="plus"
      @clicked="navigateToModal('create', customerFilter ? { customer: customerFilter } : undefined)"
    />
    <CoarMenuItem
      v-if="contextTodo && !contextTodo.ParentTodoId"
      :label="t('todo.createSubTask', {}, 'Create subtask')"
      icon="list-tree"
      @clicked="createSubTodo"
    />
    <CoarMenuDivider />
    <CoarSubFlyout :label="t('common.status', {}, 'Status')">
      <CoarMenu :show-icon-column="false">
        <CoarMenuItem :label="t('todo.statusNone', {}, '(none)')" @clicked="() => setStatus('None')" />
        <CoarMenuItem :label="t('todo.statusNew', {}, 'New')" @clicked="() => setStatus('New')" />
        <CoarMenuItem :label="t('todo.statusInProgress', {}, 'In Progress')" @clicked="() => setStatus('InProgress')" />
        <CoarMenuItem :label="t('todo.statusDone', {}, 'Done')" @clicked="() => setStatus('Done')" />
        <CoarMenuItem :label="t('todo.statusInfo', {}, 'Info')" @clicked="() => setStatus('Info')" />
      </CoarMenu>
    </CoarSubFlyout>
    <CoarSubFlyout :label="t('common.important', {}, 'Important')">
      <CoarMenu :show-icon-column="false">
        <CoarMenuItem :label="t('common.yes', {}, 'Yes')" @clicked="() => setCritical(true)" />
        <CoarMenuItem :label="t('common.no', {}, 'No')" @clicked="() => setCritical(false)" />
      </CoarMenu>
    </CoarSubFlyout>
    <CoarMenuItem
      :label="t('todo.awaitingFeedback', {}, 'Awaiting Feedback')"
      :icon="contextTodo?.AwaitingFeedback ? 'square-check' : 'square'"
      @clicked="() => setAwaitingFeedback(!contextTodo?.AwaitingFeedback)"
    />
    <CoarMenuItem
      v-if="contextTodo?.IsArchived"
      :label="t('common.restore', {}, 'Restore')"
      icon="archive-restore"
      @clicked="restoreTodos"
    />
    <CoarMenuItem
      v-if="!contextTodo?.IsArchived"
      :label="t('common.archive', {}, 'Archive')"
      icon="archive"
      @clicked="archiveTodos"
    />
    <CoarMenuDivider />
    <CoarMenuItem
      :label="t('todo.columnLayoutReset', {}, 'Reset columns')"
      icon="columns-3"
      @clicked="resetColumnState"
    />
    <CoarMenuItem
      v-if="moveMode"
      :label="t('todo.moveModeDisable', {}, 'Disable move mode')"
      icon="move"
      @clicked="toggleMoveMode"
    />
    <CoarMenuItem
      v-if="!moveMode"
      :label="t('todo.moveModeEnable', {}, 'Enable move mode')"
      icon="move"
      @clicked="toggleMoveMode"
    />
    <CoarMenuItem
      v-if="moveMode && contextTodo?.ParentTodoId"
      :label="t('todo.convertToTask', {}, 'Convert to task')"
      icon="arrow-up-from-line"
      @clicked="convertToParentTodo"
    />
    <CoarMenuDivider />
    <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteTodos" />
  </CoarContextMenu>

  <!-- Viewport context menu (empty area) -->
  <CoarContextMenu :menu="viewportMenu">
    <CoarMenuItem :label="t('common.create', {}, 'Create')" @clicked="navigateToModal('create', customerFilter ? { customer: customerFilter } : undefined)" />
  </CoarContextMenu>
</template>
