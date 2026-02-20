// UI Context types
export type {
  IUIHeader,
  IUIFooter,
  IUIButton,
  IUIContent,
  UIContext,
  UIContextInfo,
} from './ui.context';
export { UiContextDefaultState } from './ui.context';

// UI Service
export { UIService } from './ui.service';

// Modal
export type { ModalContext, ModalOptions, ComponentInputs } from './modal/modal-context';
export type { ModalOverlayRef } from './modal/modal-host-ui.service';
export { ModalHostUIService, MODAL_OVERLAY_REF } from './modal/modal-host-ui.service';
export { ModalHostComponent } from './modal/modal-host.component';
export type { ModalRef } from './modal/modal.service';
export { ModalService } from './modal/modal.service';
export { RoutedModalService } from './modal/routed-modal.service';

// Utils
export { deepClone } from './utils/deep-clone';
