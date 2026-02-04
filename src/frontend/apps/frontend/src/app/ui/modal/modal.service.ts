import { Injectable, Type, Injector, inject } from '@angular/core';
import { ComponentInputs, ModalContext, ModalOptions } from './modal-context';
import { ModalHostComponent } from './modal-host.component';
import { MODAL_OVERLAY_REF, ModalOverlayRef } from './modal-host-ui.service';

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
  private injector = inject(Injector);

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
    // Create the modal context
    const context: ModalContext<T> = {
      innerComponent: component,
      inputs,
      title: options.title,
      subTitle: options.subTitle,
    };

    // Track close handlers
    let closeResolve: (result: unknown) => void;
    const afterClosedPromise = new Promise<unknown>((resolve) => {
      closeResolve = resolve;
    });

    // Create overlay ref
    const overlayRef: ModalOverlayRef = {
      close: (result?: unknown) => {
        // Remove the modal element
        if (modalElement) {
          modalElement.remove();
        }
        if (backdropElement) {
          backdropElement.remove();
        }
        closeResolve(result);
      },
    };

    // Create backdrop
    const backdropElement = document.createElement('div');
    backdropElement.className = 'modal-backdrop';
    backdropElement.style.cssText = `
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.5);
      z-index: 1000;
      display: flex;
      align-items: center;
      justify-content: center;
    `;

    if (options.closeOnBackdropClick !== false) {
      backdropElement.addEventListener('click', (e) => {
        if (e.target === backdropElement) {
          overlayRef.close();
        }
      });
    }

    // Create modal container
    const modalElement = document.createElement('div');
    modalElement.className = 'modal-wrapper';
    modalElement.style.cssText = `
      width: ${options.width || '500px'};
      max-width: 90vw;
      max-height: 90vh;
      background: var(--color-background-primary, #fff);
      border-radius: var(--radius-lg, 8px);
      box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.25);
      overflow: hidden;
    `;

    if (options.height) {
      modalElement.style.height = options.height;
    }

    backdropElement.appendChild(modalElement);
    document.body.appendChild(backdropElement);

    // Note: In a real implementation, you would use Angular's ComponentFactoryResolver
    // or ViewContainerRef to create the ModalHostComponent dynamically.
    // This simplified version creates the DOM structure but doesn't bootstrap Angular components.
    // For full functionality, integrate with @cocoar/ui-overlay or CDK overlay.

    // For now, we'll provide a placeholder that logs the intent
    console.warn(
      'ModalService.openModal: Full Angular component creation requires integration with ' +
      '@cocoar/ui-overlay or Angular CDK. This is a placeholder implementation.'
    );

    // Return the modal ref
    return {
      close: (result?: unknown) => overlayRef.close(result),
      afterClosed: () => afterClosedPromise as Promise<unknown>,
    };
  }
}
