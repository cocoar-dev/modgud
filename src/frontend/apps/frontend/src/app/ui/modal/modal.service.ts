import { Injectable, Type } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { createOverlayBuilder, coarModalPreset } from '@cocoar/ui/overlay';
import { ComponentInputs, ModalContext, ModalOptions } from './modal-context';
import { ModalHostComponent } from './modal-host.component';

/**
 * Result from opening a modal.
 */
export interface ModalRef<T = unknown> {
  /** Close the modal with an optional result */
  close(result?: T): void;
  /** Promise that resolves when the modal is closed */
  afterClosed(): Promise<T | undefined>;
}

/**
 * Service for opening components as modals.
 *
 * @example
 * ```typescript
 * const modalRef = this.modalService.openModal(UserFormComponent, { userId: '123' });
 * const result = await modalRef.afterClosed();
 * ```
 */
@Injectable({ providedIn: 'root' })
export class ModalService {
  /**
   * Open a component as a modal.
   *
   * @param component The component type to render inside the modal
   * @param inputs Input properties to pass to the component
   * @param options Modal configuration options
   * @returns A ModalRef to control the modal
   */
  public openModal<T>(
    component: Type<T>,
    inputs: ComponentInputs<T> = {},
    options: ModalOptions = {}
  ): ModalRef {
    // Start from the modal preset
    const builder = createOverlayBuilder(coarModalPreset);

    // Apply size overrides
    if (options.width || options.height) {
      builder.size({
        mode: 'content-clamped' as const,
        ...(options.width ? { maxWidth: parseInt(options.width) || 'viewport' } : {}),
        ...(options.height ? { maxHeight: parseInt(options.height) || 'viewport' } : {}),
      });
    }

    // Apply backdrop overrides
    if (options.closeOnBackdropClick === false) {
      builder.backdrop({ kind: 'modal' as const, closeOnBackdropClick: false });
    }

    // Build the modal context for ModalHostComponent
    const context: ModalContext<T> = {
      innerComponent: component,
      inputs,
      title: options.title,
      subTitle: options.subTitle,
    };

    // Open ModalHostComponent as the overlay content.
    // COAR_OVERLAY_REF is automatically provided to the content component by the overlay service,
    // so ModalHostUIService can inject it as a fallback for closing the modal.
    const overlayRef = builder
      .fromComponent(ModalHostComponent)
      .open({ context } as ComponentInputs<ModalHostComponent<T>>);

    // Bridge OverlayRef → ModalRef
    return {
      close: (result?: unknown) => overlayRef.close(result),
      afterClosed: () => firstValueFrom(overlayRef.afterClosed$) as Promise<unknown>,
    };
  }
}
