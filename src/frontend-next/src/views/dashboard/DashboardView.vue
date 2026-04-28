<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { CoarCard, CoarIcon } from '@cocoar/vue-ui'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  criticalColumn, awaitingFeedbackColumn, titleColumn,
  statusColumn, commentsColumn, dueDateColumn, responsiblesColumn, customerColumn,
} from '@/views/todo/composables/useTodoGridColumns'
import { useUI } from '@/composables/useUI'
import { useAuthStore } from '@/stores/auth.store'
import { useTodoStore } from '@/stores/todo.store'
import { useCustomerStore } from '@/stores/customer.store'
import type { TodoListDto } from '@/models/todo'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const authStore = useAuthStore()
const todoStore = useTodoStore()
const customerStore = useCustomerStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('dashboard.title', {}, 'Dashboard')
  ctx.header.icon = 'layout-dashboard'
  ctx.content.container = true
}), { immediate: true })

onMounted(async () => {
  await Promise.all([
    todoStore.initialize(),
    customerStore.initialize(),
  ])
})

const currentUserId = computed(() => authStore.user?.Id)

// ── Filtered lists ──

const myTodos = computed(() =>
  todoStore.todoslist.filter(t =>
    t.Status !== 'Done' &&
    t.Responsibles?.some(r => r.Id === currentUserId.value)
  )
)

const overdueTodos = computed(() => {
  const now = new Date()
  now.setHours(0, 0, 0, 0)
  return todoStore.todoslist.filter(t =>
    t.DueDate &&
    t.Status !== 'Done' &&
    new Date(t.DueDate) < now
  )
})

const dueSoonTodos = computed(() => {
  const now = new Date()
  now.setHours(0, 0, 0, 0)
  const inWeek = new Date(now)
  inWeek.setDate(inWeek.getDate() + 7)
  return todoStore.todoslist.filter(t =>
    t.DueDate &&
    t.Status !== 'Done' &&
    new Date(t.DueDate) >= now &&
    new Date(t.DueDate) <= inWeek
  )
})

const awaitingFeedbackTodos = computed(() =>
  todoStore.todoslist.filter(t => t.AwaitingFeedback && t.Status !== 'Done')
)

const unreadCommentsCount = computed(() =>
  todoStore.todoslist.reduce((sum, t) => sum + (t.UnreadComments || 0) + (t.ChildTodosUnreadCommentsCount || 0), 0)
)

const unreadCommentsTodos = computed(() =>
  todoStore.todoslist.filter(t => (t.UnreadComments || 0) + (t.ChildTodosUnreadCommentsCount || 0) > 0)
)

// ── KPI detail filter ──

type KpiFilter = 'myTasks' | 'overdue' | 'dueSoon' | 'feedback' | 'unread' | null
const activeFilter = ref<KpiFilter>(null)

function toggleFilter(filter: KpiFilter) {
  activeFilter.value = activeFilter.value === filter ? null : filter
  // Auto-expand parents that contain matched children
  if (activeFilter.value) {
    const matched = filteredTodos.value
    const parentIds = new Set<string>()
    for (const t of matched) {
      if (t.ParentTodoId) parentIds.add(t.ParentTodoId)
    }
    filterOpenRows.value = [...parentIds]
  }
}

const filteredTodos = computed(() => {
  switch (activeFilter.value) {
    case 'myTasks': return myTodos.value
    case 'overdue': return overdueTodos.value
    case 'dueSoon': return dueSoonTodos.value
    case 'feedback': return awaitingFeedbackTodos.value
    case 'unread': return unreadCommentsTodos.value
    default: return []
  }
})

const filterLabel = computed(() => {
  switch (activeFilter.value) {
    case 'myTasks': return t('dashboard.myTasks', {}, 'My open tasks')
    case 'overdue': return t('dashboard.overdue', {}, 'Overdue')
    case 'dueSoon': return t('dashboard.dueThisWeek', {}, 'Due this week')
    case 'feedback': return t('dashboard.awaitingFeedback', {}, 'Awaiting feedback')
    case 'unread': return t('dashboard.unreadComments', {}, 'Unread comments')
    default: return ''
  }
})

// ── KPI Grid (tree data) ──

interface TodoTreeNode extends TodoListDto {
  children: TodoTreeNode[]
}

// IDs of items that directly match the active filter (not context parents)
const filterMatchedIds = computed(() => new Set(filteredTodos.value.map(t => t.Id)))

// Build tree: matched items + their parents (for tree context only)
const filterTreeData = computed<TodoTreeNode[]>(() => {
  const matched = filteredTodos.value
  if (!matched.length) return []

  const matchedIds = filterMatchedIds.value
  const allTodos = todoStore.todoslist
  const includeIds = new Set(matchedIds)

  // Include missing parents for tree structure
  for (const t of matched) {
    if (t.ParentTodoId && !includeIds.has(t.ParentTodoId)) {
      includeIds.add(t.ParentTodoId)
    }
  }

  const allItems = allTodos.filter(t => includeIds.has(t.Id))

  // Build tree
  const childrenMap = new Map<string, TodoTreeNode[]>()
  const parents: TodoTreeNode[] = []

  for (const t of allItems) {
    const node: TodoTreeNode = { ...t, children: [] }
    if (t.ParentTodoId) {
      const siblings = childrenMap.get(t.ParentTodoId) || []
      siblings.push(node)
      childrenMap.set(t.ParentTodoId, siblings)
    } else {
      parents.push(node)
    }
  }

  for (const p of parents) {
    p.children = childrenMap.get(p.Id) || []
  }

  return parents
})

const filterTreeDataRef = ref<TodoTreeNode[]>([])
watch(filterTreeData, (val) => { filterTreeDataRef.value = val }, { immediate: true })

const filterOpenRows = ref<string[]>([])

const filterGridBuilder = CoarGridBuilder.create<TodoTreeNode>()
  .treeData({
    children: (row) => row.children,
    rowId: (row) => row.Id,
  })
  .openRows(filterOpenRows)
  .rowDataRef(filterTreeDataRef)
  .searchHighlight()
  .rowSelection('single')
  .rowClassRules({
    'status-done': (p) => p.data?.Status === 'Done',
    'status-inProgress': (p) => p.data?.Status === 'InProgress',
    'infoTodo': (p) => p.data?.Status === 'Info',
    'childTodo': (p) => !!p.data?.ParentTodoId,
    'parentTodo': (p) => !p.data?.ParentTodoId,
    'kpi-context-row': (p) => !!p.data && !filterMatchedIds.value.has(p.data.Id),
  })
  .onCellDoubleClicked((event) => {
    if (event.data) {
      const colId = event.column?.getColId() ?? event.colDef?.colId
      const params = colId === 'CommentsCount' ? { tab: 'comments' } : undefined
      navigateToModal(event.data.Id, params)
    }
  })
  .columns([
    criticalColumn(),
    awaitingFeedbackColumn(),
    titleColumn(),
    statusColumn(),
    commentsColumn(),
    dueDateColumn(),
    responsiblesColumn(),
    customerColumn(),
  ])

// ── Urgent list (overdue + due soon, sorted by date) ──

const urgentTodos = computed(() => {
  const combined = [...overdueTodos.value, ...dueSoonTodos.value]
  // Deduplicate
  const seen = new Set<string>()
  const unique = combined.filter(t => {
    if (seen.has(t.Id)) return false
    seen.add(t.Id)
    return true
  })
  return unique.sort((a, b) => {
    const da = a.DueDate ? new Date(a.DueDate).getTime() : Infinity
    const db = b.DueDate ? new Date(b.DueDate).getTime() : Infinity
    return da - db
  }).slice(0, 10)
})

// ── Recent activity ──

const recentTodos = computed(() =>
  [...todoStore.todoslist]
    .filter(t => t.LastTouchedAt)
    .sort((a, b) => new Date(b.LastTouchedAt!).getTime() - new Date(a.LastTouchedAt!).getTime())
    .slice(0, 10)
)

// ── Helpers ──

function isOverdue(todo: TodoListDto): boolean {
  if (!todo.DueDate) return false
  const now = new Date()
  now.setHours(0, 0, 0, 0)
  return new Date(todo.DueDate) < now
}

function formatDate(dateStr: string): string {
  const d = new Date(dateStr)
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}.${d.getFullYear()}`
}

function formatDateTime(dateStr: string): string {
  const d = new Date(dateStr)
  return `${formatDate(dateStr)} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

function daysUntil(dateStr: string): number {
  const now = new Date()
  now.setHours(0, 0, 0, 0)
  const target = new Date(dateStr)
  target.setHours(0, 0, 0, 0)
  return Math.round((target.getTime() - now.getTime()) / (1000 * 60 * 60 * 24))
}

function openTodo(id: string) {
  navigateToModal(id)
}
</script>

<template>
  <div class="w-full py-6 space-y-6">
    <!-- KPI Cards -->
    <div class="grid grid-cols-2 gap-4 lg:grid-cols-5">
      <!-- My open tasks -->
      <CoarCard elevated variant="info" class="kpi-card" :class="{ 'kpi-card--active': activeFilter === 'myTasks' }" @click="toggleFilter('myTasks')">
        <div class="kpi-content">
          <div class="kpi-icon kpi-icon--blue">
            <CoarIcon name="user" size="m" />
          </div>
          <div class="kpi-value">{{ myTodos.length }}</div>
          <div class="kpi-label">{{ t('dashboard.myTasks', {}, 'My open tasks') }}</div>
        </div>
      </CoarCard>

      <!-- Overdue -->
      <CoarCard elevated variant="info" class="kpi-card" :class="{ 'kpi-card--active': activeFilter === 'overdue' }" @click="toggleFilter('overdue')">
        <div class="kpi-content">
          <div class="kpi-icon kpi-icon--red">
            <CoarIcon name="clock" size="m" />
          </div>
          <div class="kpi-value" :class="{ 'text-red-500': overdueTodos.length > 0 }">{{ overdueTodos.length }}</div>
          <div class="kpi-label">{{ t('dashboard.overdue', {}, 'Overdue') }}</div>
        </div>
      </CoarCard>

      <!-- Due this week -->
      <CoarCard elevated variant="info" class="kpi-card" :class="{ 'kpi-card--active': activeFilter === 'dueSoon' }" @click="toggleFilter('dueSoon')">
        <div class="kpi-content">
          <div class="kpi-icon kpi-icon--orange">
            <CoarIcon name="clock" size="m" />
          </div>
          <div class="kpi-value">{{ dueSoonTodos.length }}</div>
          <div class="kpi-label">{{ t('dashboard.dueThisWeek', {}, 'Due this week') }}</div>
        </div>
      </CoarCard>

      <!-- Awaiting feedback -->
      <CoarCard elevated variant="info" class="kpi-card" :class="{ 'kpi-card--active': activeFilter === 'feedback' }" @click="toggleFilter('feedback')">
        <div class="kpi-content">
          <div class="kpi-icon kpi-icon--purple">
            <CoarIcon name="circle-help" size="m" />
          </div>
          <div class="kpi-value">{{ awaitingFeedbackTodos.length }}</div>
          <div class="kpi-label">{{ t('dashboard.awaitingFeedback', {}, 'Awaiting feedback') }}</div>
        </div>
      </CoarCard>

      <!-- Unread comments -->
      <CoarCard elevated variant="info" class="kpi-card" :class="{ 'kpi-card--active': activeFilter === 'unread' }" @click="toggleFilter('unread')">
        <div class="kpi-content">
          <div class="kpi-icon kpi-icon--green">
            <CoarIcon name="messages-square" size="m" />
          </div>
          <div class="kpi-value">{{ unreadCommentsCount }}</div>
          <div class="kpi-label">{{ t('dashboard.unreadComments', {}, 'Unread comments') }}</div>
        </div>
      </CoarCard>
    </div>

    <!-- KPI Detail Grid -->
    <div v-if="activeFilter">
      <div class="flex items-center justify-between mb-2">
        <h3 class="text-sm font-semibold uppercase tracking-wider text-surface-500">
          {{ filterLabel }} ({{ filteredTodos.length }})
        </h3>
        <button class="text-surface-400 hover:text-surface-600 transition" @click="activeFilter = null">
          <CoarIcon name="x" size="s" />
        </button>
      </div>
      <div v-if="filteredTodos.length === 0" class="text-sm text-surface-400 py-4 text-center">
        {{ t('dashboard.noResults', {}, 'No tasks found') }}
      </div>
      <div v-else style="height: 400px">
        <CoarDataGrid
          :builder="filterGridBuilder"
          show-search
          class="h-full"
          bordered
          elevated
        />
      </div>
    </div>

    <!-- Lists -->
    <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
      <!-- Urgent: overdue + due soon -->
      <CoarCard elevated>
        <div class="p-4">
          <h3 class="text-sm font-semibold uppercase tracking-wider text-surface-500 mb-3">
            <CoarIcon name="alert-triangle" size="s" class="mr-1 inline-block align-text-bottom" />
            {{ t('dashboard.urgentTitle', {}, 'Overdue & due soon') }}
          </h3>
          <div v-if="urgentTodos.length === 0" class="text-sm text-surface-400 py-4 text-center">
            {{ t('dashboard.noUrgent', {}, 'No urgent tasks — well done!') }}
          </div>
          <div v-else class="space-y-1">
            <button
              v-for="todo in urgentTodos"
              :key="todo.Id"
              class="todo-row"
              @click="openTodo(todo.Id)"
            >
              <div class="flex items-center gap-2 flex-1 min-w-0">
                <CoarIcon
                  v-if="todo.Critical"
                  name="triangle-alert"
                  size="xs"
                  class="text-red-500 flex-shrink-0"
                />
                <span class="truncate" :class="{ 'font-semibold': !todo.ParentTodoId }">{{ todo.Title }}</span>
              </div>
              <div class="flex items-center gap-2 flex-shrink-0 text-xs">
                <span v-if="todo.Customer" class="text-surface-400">{{ todo.Customer.Label }}</span>
                <span
                  v-if="todo.DueDate"
                  class="due-badge"
                  :class="isOverdue(todo) ? 'due-badge--overdue' : 'due-badge--soon'"
                >
                  {{ formatDate(todo.DueDate) }}
                  <template v-if="isOverdue(todo)"> ({{ Math.abs(daysUntil(todo.DueDate)) }}d)</template>
                  <template v-else> ({{ daysUntil(todo.DueDate) }}d)</template>
                </span>
              </div>
            </button>
          </div>
        </div>
      </CoarCard>

      <!-- Recent activity -->
      <CoarCard elevated>
        <div class="p-4">
          <h3 class="text-sm font-semibold uppercase tracking-wider text-surface-500 mb-3">
            <CoarIcon name="clock" size="s" class="mr-1 inline-block align-text-bottom" />
            {{ t('dashboard.recentTitle', {}, 'Recent activity') }}
          </h3>
          <div v-if="recentTodos.length === 0" class="text-sm text-surface-400 py-4 text-center">
            {{ t('dashboard.noRecent', {}, 'No recent activity') }}
          </div>
          <div v-else class="space-y-1">
            <button
              v-for="todo in recentTodos"
              :key="todo.Id"
              class="todo-row"
              @click="openTodo(todo.Id)"
            >
              <div class="flex items-center gap-2 flex-1 min-w-0">
                <span class="truncate" :class="{ 'font-semibold': !todo.ParentTodoId }">{{ todo.Title }}</span>
                <span
                  v-if="todo.UnreadComments > 0"
                  class="inline-flex items-center justify-center h-4 min-w-4 px-1 rounded-full bg-orange-500 text-white text-[10px] font-bold flex-shrink-0"
                >{{ todo.UnreadComments }}</span>
              </div>
              <div class="flex items-center gap-2 flex-shrink-0 text-xs">
                <span v-if="todo.Customer" class="text-surface-400">{{ todo.Customer.Label }}</span>
                <span class="text-surface-400">{{ todo.LastTouchedAt ? formatDateTime(todo.LastTouchedAt) : '' }}</span>
              </div>
            </button>
          </div>
        </div>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.kpi-card {
  cursor: pointer;
  transition: transform 0.2s ease, box-shadow 0.2s ease;
}

.kpi-card:hover {
  transform: translateY(-3px);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.15);
}

.kpi-card--active {
  transform: translateY(-3px);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.15), 0 0 0 2px var(--coar-border-accent-bold, #3b82f6);
}

.kpi-content {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 1rem 0.5rem;
  gap: 0.25rem;
}

.kpi-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 2.5rem;
  height: 2.5rem;
  border-radius: 0.75rem;
  margin-bottom: 0.25rem;
}

.kpi-icon--blue { background: rgba(59, 130, 246, 0.15); color: #3b82f6; }
.kpi-icon--red { background: rgba(239, 68, 68, 0.15); color: #ef4444; }
.kpi-icon--orange { background: rgba(245, 158, 11, 0.15); color: #f59e0b; }
.kpi-icon--purple { background: rgba(139, 92, 246, 0.15); color: #8b5cf6; }
.kpi-icon--green { background: rgba(34, 197, 94, 0.15); color: #22c55e; }

.kpi-value {
  font-size: 1.75rem;
  font-weight: 700;
  line-height: 1;
}

.kpi-label {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  text-align: center;
}

.todo-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  width: 100%;
  padding: 0.5rem 0.5rem;
  border-radius: 0.375rem;
  text-align: left;
  font-size: 0.875rem;
  transition: background-color 0.1s;
  cursor: pointer;
  border: none;
  background: none;
  color: inherit;
}

.todo-row:hover {
  background-color: var(--coar-background-neutral-tertiary, rgba(0, 0, 0, 0.04));
}

.due-badge {
  padding: 0.125rem 0.375rem;
  border-radius: 9999px;
  font-weight: 500;
  white-space: nowrap;
}

.due-badge--overdue {
  background: rgba(239, 68, 68, 0.15);
  color: #ef4444;
}

.due-badge--soon {
  background: rgba(245, 158, 11, 0.15);
  color: #f59e0b;
}

:deep(.kpi-context-row) {
  opacity: 0.3;
}
</style>
