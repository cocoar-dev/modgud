<script setup lang="ts">
import { ref, watch, onMounted, onBeforeUnmount } from 'vue'
import { Jodit } from 'jodit'
import 'jodit/build/jodit.min.css'

const props = withDefaults(defineProps<{
  modelValue?: string
  placeholder?: string
  height?: string
  readonly?: boolean
}>(), {
  modelValue: '',
  placeholder: '',
  height: '100%',
  readonly: false,
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const editorRef = ref<HTMLTextAreaElement>()
const toolbarRef = ref<HTMLDivElement>()
let editor: Jodit | undefined

const buttons = [
  'bold',
  'italic',
  'underline',
  '|',
  'ul',
  'ol',
  '|',
  'outdent',
  'indent',
  'align',
  'font',
  'fontsize',
  'brush',
  '|',
  'eraser',
]

onMounted(() => {
  if (!editorRef.value || !toolbarRef.value) return

  editor = Jodit.make(editorRef.value, {
    zIndex: 0,
    readonly: props.readonly,
    toolbarButtonSize: 'middle',
    theme: 'default',
    allowResizeY: false,
    spellcheck: false,
    triggerChangeEvent: true,
    width: 'auto',
    height: props.height,
    minHeight: 60,
    language: 'de',
    toolbar: toolbarRef.value,
    placeholder: props.placeholder,
    statusbar: false,
    enter: 'p',
    buttons,
    buttonsMD: buttons,
    buttonsSM: buttons,
    buttonsXS: buttons,
    textIcons: false,
    className: 'dui-rte',
  } as any)

  editor.value = props.modelValue || ''

  editor.events.on('change', (newValue: string) => {
    emit('update:modelValue', newValue)
  })
})

watch(() => props.modelValue, (val) => {
  if (editor && editor.value !== val) {
    editor.value = val || ''
  }
})

watch(() => props.readonly, (val) => {
  if (editor) {
    editor.setReadOnly(val)
  }
})

onBeforeUnmount(() => {
  editor?.destruct()
  editor = undefined
})
</script>

<template>
  <div class="jodit-richtext flex flex-col flex-1 overflow-hidden">
    <div class="jodit-container flex flex-col flex-1">
      <div ref="toolbarRef"></div>
      <textarea ref="editorRef" class="w-full"></textarea>
    </div>
  </div>
</template>
