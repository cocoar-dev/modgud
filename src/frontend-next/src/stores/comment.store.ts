import { defineStore } from 'pinia'
import { useHttpClient } from '@/composables/useHttpClient'
import type { CommentListDto, CommentCreateDto } from '@/models/comment'

export const useCommentStore = defineStore('comment', () => {
  const http = useHttpClient('/api/comment')

  async function getComments(todoId: string): Promise<CommentListDto[]> {
    return http.addPath('todo', todoId).get<CommentListDto[]>()
  }

  async function addComment(type: string, referenceId: string, comment: CommentCreateDto): Promise<CommentListDto> {
    return http.addPath(type, referenceId).post<CommentListDto>(comment)
  }

  async function deleteComment(id: string): Promise<void> {
    return http.addPath(id).delete<void>()
  }

  async function confirmRead(id: string): Promise<void> {
    return http.addPath(id, 'read').post<void>()
  }

  return { getComments, addComment, deleteComment, confirmRead }
})
