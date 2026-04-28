<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import DOMPurify from 'dompurify'
import { useCommentStore } from '@/stores/comment.store'
import { useI18n } from '@cocoar/vue-localization'
import { CoarButton, CoarIcon } from '@cocoar/vue-ui'
import RichTextEditor from '@/components/RichTextEditor.vue'
import type { CommentListDto } from '@/models/comment'

const { t } = useI18n()

const props = defineProps<{ todoId: string }>()
const emit = defineEmits<{
  'count-changed': [count: number]
  'unread-changed': [delta: number]
}>()

const commentStore = useCommentStore()
const comments = ref<CommentListDto[]>([])
const newComment = ref('')
const loading = ref(false)

const sortedComments = computed(() =>
  [...comments.value].sort((a, b) => new Date(b.CreatedAt).getTime() - new Date(a.CreatedAt).getTime())
)

async function fetchComments() {
  loading.value = true
  try {
    comments.value = await commentStore.getComments(props.todoId)
    emit('count-changed', comments.value.length)
  } finally {
    loading.value = false
  }
}

async function addComment() {
  if (!newComment.value.trim()) return
  const created = await commentStore.addComment('todo', props.todoId, { Description: newComment.value })
  newComment.value = ''
  comments.value.push(created)
  emit('count-changed', comments.value.length)
}

async function deleteComment(id: string) {
  if (!confirm(t('todo.comments.confirmDelete', {}, 'Delete comment?'))) return
  const wasUnread = !comments.value.find(c => c.Id === id)?.IHaveRead
  await commentStore.deleteComment(id)
  comments.value = comments.value.filter(c => c.Id !== id)
  emit('count-changed', comments.value.length)
  if (wasUnread) emit('unread-changed', -1)
}

async function markAsRead(id: string) {
  await commentStore.confirmRead(id)
  const comment = comments.value.find(c => c.Id === id)
  if (comment && !comment.IHaveRead) {
    comment.IHaveRead = true
    emit('unread-changed', -1)
  }
}

function formatDate(dateStr: string): string {
  return new Date(dateStr).toLocaleString('de-DE', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

onMounted(fetchComments)
</script>

<template>
  <div class="flex flex-col h-full min-h-0">
    <!-- New comment input -->
    <div class="flex flex-col flex-shrink-0">
      <RichTextEditor v-model="newComment" height="200px" :placeholder="t('todo.comments.placeholder', {}, 'Comment...')" />
      <div class="my-1 flex flex-row-reverse">
        <CoarButton
          :disabled="!newComment.trim()"
          @click="addComment"
          size="s"
        >{{ t('todo.comments.add', {}, 'Add') }}</CoarButton>
      </div>
    </div>

    <!-- Comments list -->
    <div v-if="loading" class="text-center text-gray-400 py-4">{{ t('common.loading', {}, 'Loading...') }}</div>
    <div v-else class="flex-1 overflow-y-auto min-h-0">
      <div
        v-for="comment in sortedComments"
        :key="comment.Id"
        class="mb-2 bg-white p-2 hover:bg-gray-100 group"
      >
        <div class="p-2 pb-2">
          <div class="flex items-baseline">
            <span class="text-xs text-gray-400">{{ formatDate(comment.CreatedAt) }}</span>
            <CoarIcon
              v-if="!comment.IHaveRead"
              name="check"
              size="xs"
              color="#f97316"
              class="ml-1 cursor-pointer scale-125"
              :title="t('todo.comments.markAsRead', {}, 'mark as read')"
              @click="markAsRead(comment.Id)"
            />
            <div class="flex-1"></div>
            <span class="text-xs text-gray-400">({{ comment.CreatedBy?.Label || '?' }})</span>
          </div>

          <div class="flex mt-1">
            <div class="w-5 max-w-5 flex flex-col">
              <CoarIcon
                name="trash-2"
                size="xs"
                class="invisible group-hover:visible cursor-pointer opacity-20 hover:text-red-900 hover:opacity-100"
                @click="deleteComment(comment.Id)"
              />
            </div>
            <div class="ml-1 text-sm" v-html="DOMPurify.sanitize(comment.Description)"></div>
          </div>
        </div>
      </div>
      <div v-if="comments.length === 0" class="text-center text-gray-400 py-4">
        {{ t('todo.comments.noComments', {}, 'No comments available.') }}
      </div>
    </div>
  </div>
</template>
