<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { CoarIcon } from '@cocoar/vue-ui'
import { useInboxStore } from '@/stores/inbox.store'
import InboxPanel from './InboxPanel.vue'

const inboxStore = useInboxStore()
const open = ref(false)
const buttonRef = ref<HTMLButtonElement | null>(null)

// Lazy initialization on first mount — store is only built once Pinia
// resolves it; placing initialize() here means the bell is the only thing
// that needs to be in the tree for inbox to start syncing.
onMounted(async () => {
  if (!inboxStore.allLoaded) {
    try {
      await inboxStore.initialize()
      // initialize() with loadOnInit:false skips loadAll; we want the data,
      // so trigger explicitly here. Future reconnects re-fetch automatically
      // via runOnEveryReconnect inside useEntityService.
      await inboxStore.loadAll()
    } catch (err) {
      // Non-fatal — bell will just be empty until next reconnect.
      console.error('[InboxBell] Failed to load inbox:', err)
    }
  }
})

function toggle() {
  open.value = !open.value
}

function close() {
  open.value = false
}

// Close on outside click. Single document listener while panel is open.
function onDocClick(event: MouseEvent) {
  if (!open.value) return
  const target = event.target as Node
  if (buttonRef.value && buttonRef.value.contains(target)) return
  // The panel itself stops propagation on its root, so any click here
  // means the user clicked outside both the button and the panel.
  close()
}

onMounted(() => document.addEventListener('mousedown', onDocClick))
onUnmounted(() => document.removeEventListener('mousedown', onDocClick))
</script>

<template>
  <div class="relative">
    <button
      ref="buttonRef"
      class="bell-btn"
      :title="`Inbox (${inboxStore.unreadCount} unread)`"
      @click="toggle"
    >
      <CoarIcon name="bell" />
      <span v-if="inboxStore.unreadCount > 0" class="bell-badge">
        {{ inboxStore.unreadCount > 99 ? '99+' : inboxStore.unreadCount }}
      </span>
    </button>
    <InboxPanel v-if="open" :close="close" class="inbox-panel-anchor" />
  </div>
</template>

<style scoped>
.bell-btn {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  height: 2.25rem;
  width: 2.25rem;
  border-radius: 9999px;
  color: white;
  background: rgba(255, 255, 255, 0);
  transition: background 0.15s ease;
}

.bell-btn:hover {
  background: rgba(255, 255, 255, 0.15);
}

.bell-badge {
  position: absolute;
  top: 0.1rem;
  right: 0.05rem;
  min-width: 1.1rem;
  height: 1.1rem;
  padding: 0 0.3rem;
  border-radius: 9999px;
  background: var(--coar-background-semantic-error-bold, #dc2626);
  color: white;
  font-size: 0.7rem;
  font-weight: 700;
  line-height: 1.1rem;
  text-align: center;
  border: 2px solid var(--color-header, #1f2937);
  box-sizing: content-box;
}

.inbox-panel-anchor {
  position: absolute;
  top: calc(100% + 0.5rem);
  right: 0;
  z-index: 100;
}
</style>
