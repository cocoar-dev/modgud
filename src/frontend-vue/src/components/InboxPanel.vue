<script setup lang="ts">
import { ref, computed } from 'vue'
import { RouterLink } from 'vue-router'
import { CoarIcon, CoarButton, CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useInboxStore } from '@/stores/inbox.store'
import type { InboxItemDto } from '@/models/InboxItem'

const props = defineProps<{
  close: () => void
}>()

const inboxStore = useInboxStore()
const { t } = useI18n()

const filter = ref<string>('all')

const filteredItems = computed(() => {
  const all = inboxStore.items
  if (filter.value === 'unread') return all.filter((i) => !i.ReadAt)
  return all
})

function onItemLinkClick(event: MouseEvent) {
  // Modifier keys + middle-click: browser opens the link in a new tab. Keep
  // the panel open in that case — the user is digging through several items
  // in one go. Plain click: Vue Router intercepts navigation (SPA), and we
  // close the panel afterwards.
  //
  // We deliberately do NOT mark the inbox item as read here. If the user
  // opens the modal and immediately closes it, the badge stays — which is
  // honest: they haven't really seen anything yet.
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.button === 1) return
  props.close()
}

function onDismiss(item: InboxItemDto, event: MouseEvent) {
  event.stopPropagation()
  inboxStore.dismiss(item.Id).catch((err) => console.error('dismiss failed', err))
}

function relativeTime(iso: string): string {
  const then = new Date(iso).getTime()
  const now = Date.now()
  const diff = Math.max(0, now - then)
  const sec = Math.floor(diff / 1000)
  if (sec < 60) return t('inbox.timeNow', {}, 'just now')
  const min = Math.floor(sec / 60)
  if (min < 60) return `${min} min`
  const hr = Math.floor(min / 60)
  if (hr < 24) return `${hr} h`
  const days = Math.floor(hr / 24)
  if (days < 7) return `${days} d`
  return new Date(iso).toLocaleDateString()
}

function renderTitle(item: InboxItemDto): string {
  // Translation key takes precedence; fall back to the raw key if the i18n
  // file doesn't have it yet (so the user still sees something meaningful).
  return t(item.TitleKey, asI18nParams(item.Params), item.TitleKey)
}

function renderBody(item: InboxItemDto): string {
  if (!item.BodyKey) return ''
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
</script>

<template>
  <div class="inbox-panel" @mousedown.stop @click.stop>
    <header class="panel-header">
      <div class="panel-title">{{ t('inbox.title', {}, 'Inbox') }}</div>
      <CoarTabGroup v-model="filter">
        <CoarTab id="all">{{ t('inbox.tabAll', {}, 'All') }}</CoarTab>
        <CoarTab id="unread">
          {{ t('inbox.tabUnread', {}, 'Unread') }}
          <span v-if="inboxStore.unreadCount > 0" class="tab-counter">{{ inboxStore.unreadCount }}</span>
        </CoarTab>
      </CoarTabGroup>
    </header>

    <div v-if="filteredItems.length === 0" class="empty-state">
      <CoarIcon name="inbox" size="l" />
      <p>{{ t('inbox.empty', {}, 'No notifications') }}</p>
    </div>

    <ul v-else class="item-list">
      <li
        v-for="item in filteredItems"
        :key="item.Id"
        class="item"
        :class="{ unread: !item.ReadAt }"
      >
        <!-- Linkable content (RouterLink renders as <a href>, so right-click /
             middle-click / Ctrl-click work natively). Items without a Link
             fall back to a plain div — clicking does nothing visible. -->
        <component
          :is="item.Link ? RouterLink : 'div'"
          v-bind="item.Link ? { to: item.Link } : {}"
          class="item-link"
          @click="item.Link ? onItemLinkClick($event) : null"
        >
          <div class="item-icon">
            <CoarIcon :name="item.Icon || 'bell'" size="s" />
            <span v-if="!item.ReadAt" class="unread-dot" />
          </div>
          <div class="item-body">
            <div class="item-title" v-text="renderTitle(item)" />
            <div v-if="item.BodyKey" class="item-text" v-text="renderBody(item)" />
            <div class="item-meta">
              <span class="item-time">{{ relativeTime(item.CreatedAt) }}</span>
            </div>
          </div>
        </component>
        <button class="item-dismiss" :title="t('inbox.dismiss', {}, 'Dismiss')" @click="onDismiss(item, $event)">
          <CoarIcon name="x" size="xs" />
        </button>
      </li>
    </ul>

    <footer v-if="inboxStore.items.length > 0" class="panel-footer">
      <CoarButton
        v-if="inboxStore.unreadCount > 0"
        size="s"
        variant="ghost"
        @click="inboxStore.markAllRead()"
      >
        {{ t('inbox.markAllRead', {}, 'Mark all as read') }}
      </CoarButton>
      <div class="flex-1"></div>
      <CoarButton size="s" variant="ghost" @click="inboxStore.dismissAll()">
        {{ t('inbox.dismissAll', {}, 'Clear all') }}
      </CoarButton>
    </footer>
  </div>
</template>

<style scoped>
.inbox-panel {
  width: 26rem;
  max-height: 32rem;
  background: var(--coar-background-neutral-primary, white);
  color: var(--coar-text-neutral-primary, #111);
  border: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  border-radius: 0.5rem;
  box-shadow: 0 12px 24px rgba(0, 0, 0, 0.18);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.panel-header {
  display: flex;
  flex-direction: column;
  padding: 0.75rem 1rem 0;
  border-bottom: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
}

.panel-title {
  font-size: 0.95rem;
  font-weight: 600;
  margin-bottom: 0.5rem;
}

.tab-counter {
  font-size: 0.7rem;
  font-weight: 700;
  background: var(--coar-background-semantic-error-bold, #dc2626);
  color: white;
  padding: 0 0.4rem;
  border-radius: 9999px;
  min-width: 1.1rem;
  line-height: 1.1rem;
  text-align: center;
}

.empty-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 2rem 1rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  gap: 0.5rem;
}

.item-list {
  list-style: none;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  flex: 1;
}

.item {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.6rem 1rem;
  border-bottom: 1px solid var(--coar-border-neutral-subtle, #f3f4f6);
  position: relative;
}

.item:hover {
  background: var(--coar-background-neutral-secondary, #f9fafb);
}

.item.unread {
  background: rgba(59, 130, 246, 0.04);
}

.item.unread:hover {
  background: rgba(59, 130, 246, 0.08);
}

/* RouterLink renders as <a> by default — strip the link styling so it looks
   like the rest of the list. The link covers the icon + body area; the
   dismiss button is a sibling so it gets its own click handling. */
.item-link {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  flex: 1;
  min-width: 0;
  cursor: pointer;
  color: inherit;
  text-decoration: none;
}

.item-link:hover,
.item-link:focus-visible {
  color: inherit;
  text-decoration: none;
}

.item-icon {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 2rem;
  height: 2rem;
  border-radius: 9999px;
  background: var(--coar-background-neutral-secondary, #f3f4f6);
  color: var(--coar-text-neutral-primary, #111);
  flex-shrink: 0;
}

.unread-dot {
  position: absolute;
  top: 0;
  right: 0;
  width: 0.55rem;
  height: 0.55rem;
  border-radius: 9999px;
  background: var(--coar-background-semantic-info-bold, #3b82f6);
  border: 2px solid var(--coar-background-neutral-primary, white);
  box-sizing: content-box;
}

.item-body {
  flex: 1;
  min-width: 0;
}

.item-title {
  font-size: 0.85rem;
  font-weight: 500;
  white-space: pre-wrap;
  word-break: break-word;
}

.item-text {
  font-size: 0.8rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-top: 0.15rem;
  white-space: pre-wrap;
  word-break: break-word;
}

.item-meta {
  font-size: 0.7rem;
  color: var(--coar-text-neutral-tertiary, #9ca3af);
  margin-top: 0.25rem;
}

.item-dismiss {
  opacity: 0;
  flex-shrink: 0;
  width: 1.5rem;
  height: 1.5rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 0.25rem;
  color: var(--coar-text-neutral-tertiary, #9ca3af);
}

.item:hover .item-dismiss {
  opacity: 1;
}

.item-dismiss:hover {
  background: var(--coar-background-neutral-tertiary, #e5e7eb);
  color: var(--coar-text-neutral-primary, #111);
}

.panel-footer {
  display: flex;
  align-items: center;
  padding: 0.4rem 0.5rem;
  gap: 0.25rem;
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  background: var(--coar-background-neutral-secondary, #f9fafb);
}

.flex-1 { flex: 1; }
</style>
