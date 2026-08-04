import { computed, onBeforeUnmount, onMounted, ref, type ComputedRef, type Ref } from 'vue'
import {
  breakpointForWidth,
  definePageRuntimeHost,
  usePageCodeRuntime,
  type PageNode,
} from '@cocoar/vue-page-builder'

// One host catalog for the whole security domain. Auth page documents receive
// no ambient network/storage capabilities; all privileged effects remain
// explicit CoarPageRenderer host actions owned by the corresponding view.
const authRuntimeHost = definePageRuntimeHost({})

export function useAuthPageCodeRuntime(options: {
  pageId: string
  schema: Ref<PageNode> | ComputedRef<PageNode>
  context: ComputedRef<Record<string, unknown>>
}) {
  const width = ref(typeof window === 'undefined' ? 1280 : window.innerWidth)
  const updateWidth = () => { width.value = window.innerWidth }

  onMounted(() => window.addEventListener('resize', updateWidth, { passive: true }))
  onBeforeUnmount(() => window.removeEventListener('resize', updateWidth))

  const viewport = computed(() => ({
    width: width.value,
    breakpoint: breakpointForWidth(width.value),
  }))

  return usePageCodeRuntime({
    pageId: computed(() => options.pageId),
    schema: options.schema,
    context: options.context,
    viewport,
    runtimeHost: authRuntimeHost,
  })
}
