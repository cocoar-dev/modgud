import { TemplateRef } from '@angular/core';

export type IUIHeader = {
  show: boolean;
  title?: string;
  subTitle?: string;
  icon?: string;
  outlet?: TemplateRef<unknown>;
};

export type IUIButton = {
  text?: string;
  disabled?: boolean;
  loading?: boolean;
  visible?: boolean;
  onClick?: () => void;
};

export type IUIFooter = {
  show: boolean;
  button1: IUIButton;
  button2: IUIButton;
  button3: IUIButton;
  outlet?: TemplateRef<unknown>;
  useTemplate?: TemplateRef<unknown>;
};

export type IUIContent = {
  scrollable: boolean;
  container: boolean;
  showAlways: boolean;
  showLoadingBar: boolean;
  showSidebar: boolean;
  onContextMenu?: ($event: MouseEvent) => void;
  onClick?: ($event: MouseEvent) => void;
};

export type UIContext = {
  header: IUIHeader;
  content: IUIContent;
  footer: IUIFooter;
};

export const UiContextDefaultState: UIContext = {
  header: {
    show: true,
    title: undefined,
    subTitle: undefined,
    icon: undefined,
    outlet: undefined,
  },
  content: {
    scrollable: true,
    container: true,
    showAlways: false,
    showLoadingBar: false,
    showSidebar: false,
    onContextMenu: undefined,
    onClick: undefined,
  },
  footer: {
    show: false,
    button1: {
      text: undefined,
      disabled: false,
      loading: false,
      visible: false,
      onClick: undefined,
    },
    button2: {
      text: undefined,
      disabled: false,
      loading: false,
      visible: false,
      onClick: undefined,
    },
    button3: {
      text: undefined,
      disabled: false,
      loading: false,
      visible: false,
      onClick: undefined,
    },
    outlet: undefined,
    useTemplate: undefined,
  },
};

export interface UIContextInfo {
  type: 'page' | 'modal';
}
