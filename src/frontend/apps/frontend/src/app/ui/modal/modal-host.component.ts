import {
  Component,
  Injector,
  Type,
  ViewChild,
  ViewContainerRef,
  AfterViewInit,
  inject,
  input,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { UIService } from '../ui.service';
import { ModalHostUIService, MODAL_OVERLAY_REF, ModalOverlayRef } from './modal-host-ui.service';
import { ComponentInputs, ModalContext } from './modal-context';
import { CoarButtonComponent, CoarIconComponent } from '@cocoar/ui';

/**
 * Modal host component that wraps child components in a modal container.
 * Provides header, content area, and footer based on UIService state.
 */
@Component({
  selector: 'app-modal-host',
  standalone: true,
  imports: [CommonModule, CoarButtonComponent, CoarIconComponent],
  templateUrl: './modal-host.component.html',
  styleUrl: './modal-host.component.css',
  providers: [
    ModalHostUIService,
    { provide: UIService, useExisting: ModalHostUIService },
  ],
})
export class ModalHostComponent<T> implements AfterViewInit {
  @ViewChild('contentContainer', { read: ViewContainerRef })
  contentContainer!: ViewContainerRef;

  /** The modal context containing the inner component and inputs */
  context = input.required<ModalContext<T>>();

  /** Optional overlay reference for closing the modal */
  overlayRef = input<ModalOverlayRef>();

  public ui = inject(UIService);
  private injector = inject(Injector);

  ngAfterViewInit(): void {
    this.createInnerComponent();
  }

  private createInnerComponent(): void {
    const ctx = this.context();
    if (!ctx?.innerComponent) {
      return;
    }

    // Create a custom injector that provides the UIService and overlay ref
    const customInjector = Injector.create({
      providers: [
        { provide: MODAL_OVERLAY_REF, useValue: this.overlayRef() },
      ],
      parent: this.injector,
    });

    // Create the inner component
    const componentRef = this.contentContainer.createComponent(ctx.innerComponent, {
      injector: customInjector,
    });

    // Set inputs on the component
    if (ctx.inputs) {
      for (const [key, value] of Object.entries(ctx.inputs)) {
        componentRef.setInput(key, value);
      }
    }

    // Set initial header from context if provided
    if (ctx.title || ctx.subTitle) {
      this.ui.set((uiCtx) => {
        if (ctx.title) {
          uiCtx.header.title = ctx.title;
        }
        if (ctx.subTitle) {
          uiCtx.header.subTitle = ctx.subTitle;
        }
      });
    }
  }

  onClose(): void {
    this.ui.navigateBack();
  }

  onFooterButton1Click(): void {
    const onClick = this.ui.state.footer.button1.onClick?.();
    onClick?.();
  }

  onFooterButton2Click(): void {
    const onClick = this.ui.state.footer.button2.onClick?.();
    onClick?.();
  }

  onFooterButton3Click(): void {
    const onClick = this.ui.state.footer.button3.onClick?.();
    onClick?.();
  }
}
