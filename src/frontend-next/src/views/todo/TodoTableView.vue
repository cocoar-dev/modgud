<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useTodoStore } from '@/stores/todo.store'
import { useCustomerStore } from '@/stores/customer.store'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { CoarMenu, CoarMenuItem, CoarMenuHeading, CoarBadge } from '@cocoar/vue-ui'
import TodoGrid from './TodoGrid.vue'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()

const route = useRoute()
const router = useRouter()
const todoStore = useTodoStore()
const customerStore = useCustomerStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('todo.title', {}, 'Tasks')
  ctx.header.icon = 'list'
  ctx.content.container = false
}), { immediate: true })

const selectedCustomer = computed(() => route.query.customer as string | undefined)
const isArchiveView = computed(() => route.query.archive === 'true')

function todoCountForCustomer(customerId: string): number {
  return todoStore.todoslist.filter(t => t.Customer?.Id === customerId && t.Status !== 'None').length
}

const totalTodoCount = computed(() => todoStore.todoslist.length)

const isAllActive = computed(() => !selectedCustomer.value && !isArchiveView.value)

function selectAllCustomers() {
  router.push({ query: {} })
}

function selectCustomer(customerName: string) {
  router.push({ query: { customer: customerName } })
}

function selectArchive() {
  router.push({ query: { archive: 'true' } })
}

onMounted(async () => {
  await Promise.all([
    todoStore.initialize(),
    customerStore.initialize(),
  ])
})
</script>

<template>
  <div class="flex min-h-0 flex-1">
    <!-- Left: Customer filter menu -->
    <div class="sub-nav flex-shrink-0 p-4 flex flex-col min-h-0">
      <CoarMenu :show-icon-column="false">
        <template #header>
          <CoarMenuItem
            :class="{ 'todo-menu-item--active': isAllActive }"
            @clicked="selectAllCustomers"
          >
            <div class="flex items-center justify-between w-full">
              <span class="truncate">{{ t('todo.allCustomers', {}, 'All Customers') }}</span>
              <CoarBadge
                v-if="totalTodoCount"
                :content="totalTodoCount"
                variant="secondary"
                size="s"
              />
            </div>
          </CoarMenuItem>
        </template>

        <template v-if="customerStore.importantCustomers.length > 0">
          <CoarMenuHeading :label="t('todo.topCustomers', {}, 'Top Customers')" sticky />
          <CoarMenuItem
            v-for="c in customerStore.importantCustomers"
            :key="c.Id"
            :class="{ 'todo-menu-item--active': selectedCustomer === c.Name }"
            @clicked="selectCustomer(c.Name)"
          >
            <div class="flex items-center justify-between w-full">
              <span class="truncate">{{ c.Name }}</span>
              <CoarBadge
                v-if="todoCountForCustomer(c.Id)"
                :content="todoCountForCustomer(c.Id)"
                variant="secondary"
                size="s"
              />
            </div>
          </CoarMenuItem>
        </template>

        <template v-if="customerStore.normalCustomers.length > 0">
          <CoarMenuHeading :label="t('todo.normalCustomers', {}, 'Normal Customers')" sticky />
          <CoarMenuItem
            v-for="c in customerStore.normalCustomers"
            :key="c.Id"
            :class="{ 'todo-menu-item--active': selectedCustomer === c.Name }"
            @clicked="selectCustomer(c.Name)"
          >
            <div class="flex items-center justify-between w-full">
              <span class="truncate">{{ c.Name }}</span>
              <CoarBadge
                v-if="todoCountForCustomer(c.Id)"
                :content="todoCountForCustomer(c.Id)"
                variant="secondary"
                size="s"
              />
            </div>
          </CoarMenuItem>
        </template>

        <template #footer>
          <CoarMenuItem
            :class="{ 'todo-menu-item--active': isArchiveView }"
            @clicked="selectArchive"
          >
            <span class="truncate">{{ t('common.archive', {}, 'Archive') }}</span>
          </CoarMenuItem>
        </template>
      </CoarMenu>
    </div>

    <!-- Right: Todo Grid -->
    <div class="flex-1 flex justify-center min-w-0">
      <div class="flex w-11/12 p-4">
        <TodoGrid
          :customer-filter="selectedCustomer"
          :archive-view="isArchiveView"
          class="flex-1 min-h-0"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.sub-nav {
  width: 13rem;
  height: 100%;
  --coar-background-neutral-primary: var(--coar-background-neutral-secondary, #f7f7f7);
}

.todo-menu-item--active {
  background: var(--coar-menu-item-background-active, #eff6ff);
  color: var(--coar-menu-item-text-active, #1d4ed8);
  font-weight: 500;
  border-radius: 6px;
}
</style>
