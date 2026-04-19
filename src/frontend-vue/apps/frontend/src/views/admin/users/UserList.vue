<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarContextMenu,
  CoarMenuItem,
  useContextMenu,
} from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'
import { useModal } from '@/composables/useModal'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import UserDetails from './UserDetails.vue'

interface UserRow {
  Id: string
  UserName: string
  Email?: string | null
  FirstName?: string | null
  LastName?: string | null
  IsActive: boolean
  CreatedAt?: string | null
  ModifiedAt?: string | null
  EmailConfirmed?: boolean
  TwoFactorEnabled?: boolean
}

interface PagedResponse<T> {
  Items: T[]
  TotalCount: number
  Page: number
  PageSize: number
}

const ui = useUI()
const modal = useModal()

const users = ref<UserRow[]>([])
const loading = ref(false)
const loadError = ref<string | null>(null)
const page = ref(1)
const pageSize = ref(100)
const totalCount = ref(0)

const cellMenu = useContextMenu()
const selectedIds = ref<string[]>([])

async function loadUsers() {
  loading.value = true
  loadError.value = null
  try {
    const http = useHttpClient('/api/admin/users')
    const result = await http
      .setQueryParameter('page', String(page.value))
      .setQueryParameter('pageSize', String(pageSize.value))
      .get<PagedResponse<UserRow>>()
    users.value = result.Items
    totalCount.value = result.TotalCount
  } catch (e) {
    loadError.value = e instanceof HttpClientError
      ? `Failed to load users (HTTP ${e.status}).`
      : 'Failed to load users.'
  } finally {
    loading.value = false
  }
}

const rows = computed(() => users.value)

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-users')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => {
    if (event.data) openDetails(event.data.Id)
  })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selectedIds.value = event.api.getSelectedRows().map((r: UserRow) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    (col: any) => col.field('UserName').header('Username').flex(1).minWidth(140),
    (col: any) => col.field('Email').header('Email').flex(1),
    (col: any) => col.field('FirstName').header('First Name').flex(1),
    (col: any) => col.field('LastName').header('Last Name').flex(1),
    (col: any) => col.field('IsActive').header('Active').width(90)
      .option('valueGetter', (p: any) => p.data?.IsActive ? 'Yes' : 'No'),
    (col: any) => col.field('CreatedAt').header('Created').width(180)
      .option('valueGetter', (p: any) => p.data?.CreatedAt ? new Date(p.data.CreatedAt).toLocaleString() : ''),
  ])

function openDetails(id: string) {
  modal.open(UserDetails, { id }, { size: 'm', closeOnBackdropClick: true })
}

onMounted(() => {
  ui.set((ctx) => {
    ctx.header.title = 'Users'
    ctx.header.subTitle = 'Manage user accounts'
    ctx.header.icon = 'users'
    ctx.content.container = false
  })
  loadUsers()
})

onUnmounted(() => {
  ui.reset()
})
</script>

<template>
  <div class="list-wrap">
    <div v-if="loadError" class="load-error">{{ loadError }}</div>
    <CoarDataGrid :builder="builder" show-search class="grid flex-1 min-h-0" bordered elevated />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem label="View Details" icon="eye" @clicked="selectedIds[0] && openDetails(selectedIds[0])" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
.list-wrap {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
  padding: 1rem;
  width: 100%;
}
.grid {
  min-width: 0;
  flex: 1;
}
.load-error {
  margin-bottom: 8px;
  padding: 8px 12px;
  background: var(--coar-background-semantic-error-subtle, #fef2f2);
  color: var(--coar-text-semantic-error-bold, #b91c1c);
  border: 1px solid var(--coar-border-semantic-error, #fca5a5);
  border-radius: var(--coar-radius-m, 4px);
  font-size: 0.8125rem;
}
</style>
