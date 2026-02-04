import { Injectable, InjectionToken, inject } from '@angular/core';
import { UIService } from '../ui.service';
import { UIContextInfo } from '../ui.context';

/**
 * Injection token for the overlay reference.
 * This should be provided when opening a modal via CoarOverlayService.
 */
export const MODAL_OVERLAY_REF = new InjectionToken<ModalOverlayRef>('MODAL_OVERLAY_REF');

/**
 * Interface for the overlay reference that can close the modal.
 */
export interface ModalOverlayRef {
  close(result?: unknown): void;
}

/**
 * UIService implementation for modal contexts.
 * Provides modal-specific behavior like closing the modal on navigateBack().
 */
@Injectable()
export class ModalHostUIService extends UIService {
  protected override uiContextInfo: UIContextInfo = { type: 'modal' };

  private overlayRef = inject(MODAL_OVERLAY_REF, { optional: true });

  /**
   * In modal context, navigateBack() closes the modal instead of navigating.
   */
  public override navigateBack(): void {
    if (this.overlayRef) {
      this.overlayRef.close();
    }
  }
}
