import { defineStore } from 'pinia'
import { reactive } from 'vue'

export const useAppStore = defineStore('app', () => {
  const header = reactive({
    show: true,
    title: '',
    subTitle: '',
    icon: '',
    action: undefined as { label?: string; onClick: () => void } | undefined,
  })

  const content = reactive({
    container: true,
    scrollable: true,
  })

  const footer = reactive({
    show: false,
    button1: {
      visible: false,
      text: '',
      onClick: undefined as (() => void) | undefined,
    },
  })

  function reset() {
    header.show = true
    header.title = ''
    header.subTitle = ''
    header.icon = ''
    header.action = undefined
    content.container = true
    content.scrollable = true
    footer.show = false
    footer.button1.visible = false
    footer.button1.text = ''
    footer.button1.onClick = undefined
  }

  return { header, content, footer, reset }
})
