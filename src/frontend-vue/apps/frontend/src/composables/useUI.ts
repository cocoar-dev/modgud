import { reactive } from 'vue';

export interface UIButton {
  text?: string;
  disabled?: boolean;
  loading?: boolean;
  visible?: boolean;
  onClick?: () => void;
}

export interface UIHeader {
  show: boolean;
  title?: string;
  subTitle?: string;
  icon?: string;
}

export interface UIContent {
  scrollable: boolean;
  showLoadingBar: boolean;
  container: boolean;
  padding: boolean;
}

export interface UIFooter {
  show: boolean;
  button1: UIButton;
  button2: UIButton;
  button3: UIButton;
}

export interface UIContext {
  header: UIHeader;
  content: UIContent;
  footer: UIFooter;
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
      scrollable: true,
      showLoadingBar: false,
      container: true,
      padding: true,
    },
    footer: {
      show: false,
      button1: { visible: false, disabled: false, loading: false },
      button2: { visible: false, disabled: false, loading: false },
      button3: { visible: false, disabled: false, loading: false },
    },
  };
}

const state = reactive<UIContext>(createDefaults());

export function useUI() {
  function set(fn: (ctx: UIContext) => void) {
    // Reset to defaults first, then apply the view's config
    const defaults = createDefaults();
    Object.assign(state.header, defaults.header);
    Object.assign(state.content, defaults.content);
    Object.assign(state.footer.button1, defaults.footer.button1);
    Object.assign(state.footer.button2, defaults.footer.button2);
    Object.assign(state.footer.button3, defaults.footer.button3);
    state.footer.show = defaults.footer.show;
    fn(state);
  }

  function reset() {
    const defaults = createDefaults();
    Object.assign(state.header, defaults.header);
    Object.assign(state.content, defaults.content);
    Object.assign(state.footer.button1, defaults.footer.button1);
    Object.assign(state.footer.button2, defaults.footer.button2);
    Object.assign(state.footer.button3, defaults.footer.button3);
    state.footer.show = defaults.footer.show;
  }

  return {
    state,
    set,
    reset,
  };
}
