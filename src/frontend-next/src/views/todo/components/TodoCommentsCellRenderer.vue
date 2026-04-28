<script setup lang="ts">
import { computed } from 'vue'
import { CoarIcon } from '@cocoar/vue-ui'

const props = defineProps<{
  params: any
}>()

const todo = computed(() => props.params.data)
</script>

<template>
  <div class="flex gap-2 opacity-70" v-if="todo">
    <div class="ml-1 flex" v-if="todo.CommentsCount || todo.ChildTodosUnreadCommentsCount">
      <div class="flex items-center gap-1">
        <CoarIcon name="messages-square" size="s" />
        <span>{{ todo.CommentsCount }}</span>
      </div>
      <div v-if="todo.UnreadComments" class="flex items-center gap-1 ml-1 text-orange-500">
        <CoarIcon name="messages-square" size="s" color="#f97316" />
        <span>{{ todo.UnreadComments }}</span>
      </div>
      <div v-if="todo.ChildTodosUnreadCommentsCount" class="flex items-center gap-1 ml-1 text-[#ffb300] opacity-70" style="font-size: smaller">
        <CoarIcon name="messages-square" size="s" color="#ffb300" />
        <span>{{ todo.ChildTodosUnreadCommentsCount }}</span>
      </div>
    </div>
  </div>
</template>
