import { Type } from '@angular/core';

/**
 * Extract input property types from a component type.
 */
export type ComponentInputs<T> = {
  [K in keyof T as T[K] extends Function ? never : K]?: T[K];
};

/**
 * Context passed to the modal host component.
 */
export interface ModalContext<T> {
  innerComponent: Type<T>;
  inputs: ComponentInputs<T>;
  title?: string;
  subTitle?: string;
}

/**
 * Options for opening a modal.
 */
export interface ModalOptions {
  title?: string;
  subTitle?: string;
  width?: string;
  height?: string;
  closeOnBackdropClick?: boolean;
}
