import { type InjectionKey, inject, provide, reactive } from 'vue'

export interface UIButton {
  text?: string
  disabled?: boolean
  loading?: boolean
  visible?: boolean
  onClick?: () => void
}

export interface UIHeader {
  show: boolean
  title?: string
  subTitle?: string
  icon?: string
}

export interface UIContent {
  scrollable: boolean
  showLoadingBar: boolean
  container: boolean
  hasSubNav: boolean
}

export interface UIFooter {
  show: boolean
  button1: UIButton
  button2: UIButton
  button3: UIButton
}

export interface UIContext {
  header: UIHeader
  content: UIContent
  footer: UIFooter
}

export interface UI {
  state: UIContext
  set: (fn: (ctx: UIContext) => void) => void
  reset: () => void
}

function createDefaults(): UIContext {
  return {
    header: {
      show: true,
      title: undefined,
      subTitle: undefined,
      icon: undefined,
    },
    content: {
      scrollable: false,
      showLoadingBar: false,
      container: true,
      hasSubNav: false,
    },
    footer: {
      show: false,
      button1: { visible: false, disabled: false, loading: false },
      button2: { visible: false, disabled: false, loading: false },
      button3: { visible: false, disabled: false, loading: false },
    },
  }
}

function applyDefaults(state: UIContext) {
  const defaults = createDefaults()
  Object.assign(state.header, defaults.header)
  Object.assign(state.content, defaults.content)
  Object.assign(state.footer.button1, defaults.footer.button1)
  Object.assign(state.footer.button2, defaults.footer.button2)
  Object.assign(state.footer.button3, defaults.footer.button3)
  state.footer.show = defaults.footer.show
}

const UI_KEY: InjectionKey<UI> = Symbol('ui')

/**
 * Create and provide a new UI context.
 * Called by hosts (MainLayout, ModalLayout).
 */
export function provideUI(): UI {
  const state = reactive<UIContext>(createDefaults())

  function set(fn: (ctx: UIContext) => void) {
    applyDefaults(state)
    fn(state)
  }

  function reset() {
    applyDefaults(state)
  }

  const ui: UI = { state, set, reset }
  provide(UI_KEY, ui)
  return ui
}

/**
 * Inject the nearest UI context.
 * Called by views — works in both page and modal context.
 */
export function useUI(): UI {
  const ui = inject(UI_KEY)
  if (!ui) {
    throw new Error('useUI() requires a provideUI() ancestor (Layout or ModalLayout)')
  }
  return ui
}
