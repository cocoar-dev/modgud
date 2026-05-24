<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { CoarIcon, CoarTag, useToast } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useInboxStore } from '@/stores/inbox.store'
import type { InboxItemDto } from '@/models/InboxItem'

const { t } = useI18n()
const router = useRouter()
const toast = useToast()
const store = useInboxStore()
const open = ref(false)

onMounted(() => store.initialize())

const items = computed(() => store.items)
const unread = computed(() => store.unreadCount)

function toggle() {
  open.value = !open.value
}

function close() {
  open.value = false
}

async function onClickItem(item: InboxItemDto) {
  if (item.ReadAt == null) {
    try { await store.markRead(item.Id) } catch { /* surface via toast if it fails persistently */ }
  }
  if (item.Link) {
    close()
    router.push(item.Link)
  }
}

async function onDismiss(item: InboxItemDto, ev: Event) {
  ev.stopPropagation()
  try { await store.dismiss(item.Id) }
  catch (e: any) { toast.error(e?.message ?? String(e)) }
}

async function onMarkAllRead() {
  try { await store.markAllRead() }
  catch (e: any) { toast.error(e?.message ?? String(e)) }
}

async function onDismissAll() {
  try { await store.dismissAll() }
  catch (e: any) { toast.error(e?.message ?? String(e)) }
}

function severityVariant(s: InboxItemDto['Severity']): string {
  switch (s) {
    case 'Success': return 'success'
    case 'Warning': return 'warning'
    case 'Critical': return 'danger'
    default: return 'info'
  }
}

function fmtDate(iso: string): string {
  return new Date(iso).toLocaleString()
}

/**
 * The InboxItem's TitleKey/BodyKey reference i18n keys that live under
 * `inbox.kinds.<kindLowerCamel>.*`. If the key is not present, fall back
 * to a synthesised label so users always see something meaningful even
 * before translation files catch up.
 */
function renderTitle(item: InboxItemDto): string {
  const fallback = humanise(item.TitleKey)
  return t(item.TitleKey, asI18nParams(item.Params), fallback)
}

function renderBody(item: InboxItemDto): string | null {
  if (!item.BodyKey) return null
  return t(item.BodyKey, asI18nParams(item.Params), '')
}

function asI18nParams(p: Record<string, unknown> | null | undefined): Record<string, string> {
  if (!p) return {}
  const out: Record<string, string> = {}
  for (const k of Object.keys(p)) {
    const v = p[k]
    out[k] = v == null ? '' : String(v)
  }
  return out
}

function humanise(key: string): string {
  // "inbox.kinds.scheduledJobFailed.title" -> "Scheduled Job Failed"
  const last = key.split('.').slice(-2, -1)[0] ?? key
  return last
    .replace(/([A-Z])/g, ' $1')
    .replace(/^./, (c) => c.toUpperCase())
    .trim()
}
</script>

<template>
  <div class="inbox-bell-wrapper">
    <button
      class="bell-button"
      :title="t('inbox.bellTitle', {}, 'Inbox')"
      @click="toggle"
    >
      <CoarIcon name="bell" size="m" />
      <span v-if="unread > 0" class="badge">{{ unread > 99 ? '99+' : unread }}</span>
    </button>

    <!-- backdrop closes the panel when clicked outside -->
    <div v-if="open" class="backdrop" @click="close" />

    <div v-if="open" class="panel" role="dialog">
      <div class="panel-header">
        <h3>{{ t('inbox.title', {}, 'Inbox') }}</h3>
        <div class="panel-actions">
          <button
            v-if="unread > 0"
            class="link-btn"
            @click="onMarkAllRead"
          >{{ t('inbox.markAllRead', {}, 'Mark all read') }}</button>
          <button
            v-if="items.length > 0"
            class="link-btn"
            @click="onDismissAll"
          >{{ t('inbox.dismissAll', {}, 'Clear all') }}</button>
        </div>
      </div>

      <div v-if="items.length === 0" class="empty">
        {{ t('inbox.empty', {}, 'No notifications.') }}
      </div>

      <ul v-else class="item-list">
        <li
          v-for="item in items"
          :key="item.Id"
          class="item"
          :class="{ unread: item.ReadAt == null, [severityVariant(item.Severity)]: true }"
          @click="onClickItem(item)"
        >
          <CoarIcon :name="item.Icon" size="s" class="item-icon" />
          <div class="item-body">
            <div class="item-title">{{ renderTitle(item) }}</div>
            <div v-if="renderBody(item)" class="item-subtitle">{{ renderBody(item) }}</div>
            <div class="item-meta">
              <CoarTag :variant="severityVariant(item.Severity) as any">{{ item.Severity }}</CoarTag>
              <span class="item-time">{{ fmtDate(item.CreatedAt) }}</span>
            </div>
          </div>
          <button
            class="dismiss-btn"
            :title="t('inbox.dismiss', {}, 'Dismiss')"
            @click="onDismiss(item, $event)"
          >
            <CoarIcon name="x" size="s" />
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.inbox-bell-wrapper {
  position: relative;
}

.bell-button {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 2.25rem;
  height: 2.25rem;
  border-radius: 9999px;
  background: rgba(255, 255, 255, 0.15);
  color: white;
  transition: background 120ms ease;
}

.bell-button:hover {
  background: rgba(255, 255, 255, 0.3);
}

.badge {
  position: absolute;
  top: -2px;
  right: -2px;
  min-width: 1.1rem;
  height: 1.1rem;
  padding: 0 0.3rem;
  border-radius: 9999px;
  background: #ef4444;
  color: white;
  font-size: 0.7rem;
  font-weight: 700;
  display: flex;
  align-items: center;
  justify-content: center;
  line-height: 1;
}

.backdrop {
  position: fixed;
  inset: 0;
  z-index: 60;
}

.panel {
  position: absolute;
  top: calc(100% + 0.5rem);
  right: 0;
  z-index: 70;
  width: 22rem;
  max-height: 32rem;
  display: flex;
  flex-direction: column;
  background: var(--coar-background-neutral-secondary, #ffffff);
  border: 1px solid var(--coar-border-neutral-secondary, rgba(0, 0, 0, 0.1));
  border-radius: 0.5rem;
  box-shadow: 0 12px 32px rgba(0, 0, 0, 0.18);
  color: var(--coar-text-neutral-primary);
}

.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid var(--coar-border-neutral-secondary);
}

.panel-header h3 {
  margin: 0;
  font-size: 0.95rem;
  font-weight: 600;
}

.panel-actions {
  display: flex;
  gap: 0.5rem;
}

.link-btn {
  background: none;
  border: 0;
  color: var(--coar-text-link, #2563eb);
  font-size: 0.8rem;
  cursor: pointer;
}

.link-btn:hover { text-decoration: underline; }

.empty {
  padding: 2rem 1rem;
  text-align: center;
  color: var(--coar-text-neutral-secondary);
  font-size: 0.9rem;
}

.item-list {
  list-style: none;
  margin: 0;
  padding: 0;
  overflow-y: auto;
}

.item {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid var(--coar-border-neutral-secondary);
  cursor: pointer;
}

.item:hover {
  background: rgba(0, 0, 0, 0.03);
}

.item.unread {
  background: rgba(37, 99, 235, 0.05);
}

.item.critical {
  border-left: 3px solid #ef4444;
}

.item.warning {
  border-left: 3px solid #f59e0b;
}

.item.success {
  border-left: 3px solid #10b981;
}

.item-icon {
  flex-shrink: 0;
  margin-top: 0.15rem;
  color: var(--coar-text-neutral-secondary);
}

.item-body {
  flex: 1;
  min-width: 0;
}

.item-title {
  font-size: 0.9rem;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
}

.item.unread .item-title {
  font-weight: 600;
}

.item-subtitle {
  margin-top: 0.15rem;
  font-size: 0.8rem;
  color: var(--coar-text-neutral-secondary);
}

.item-meta {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 0.35rem;
  font-size: 0.7rem;
  color: var(--coar-text-neutral-secondary);
}

.item-time {
  font-variant-numeric: tabular-nums;
}

.dismiss-btn {
  flex-shrink: 0;
  background: none;
  border: 0;
  padding: 0.2rem;
  border-radius: 0.25rem;
  cursor: pointer;
  color: var(--coar-text-neutral-secondary);
  opacity: 0.5;
}

.dismiss-btn:hover {
  background: rgba(0, 0, 0, 0.08);
  opacity: 1;
}
</style>
