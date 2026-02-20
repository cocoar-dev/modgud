import { inject, Injectable, Injector, runInInjectionContext, Type } from '@angular/core';
import { ComponentRoutedFragment, RoutedFragmentService } from '@cocoar/ui-routing';
import { ModalOptions } from './modal-context';
import { ModalRef, ModalService } from './modal.service';

/**
 * Bridges URL fragment state to modals.
 * When a fragment matching a registered ComponentRoutedFragment appears,
 * the corresponding component is opened as a modal. When the fragment
 * is removed (or the modal is closed), the two are kept in sync.
 *
 * Instantiate by injecting this service in the layout component.
 *
 * @example Route configuration:
 * ```typescript
 * data: createRouteData<IRoutedFragmentConfig<ComponentRoutedFragment<ModalOptions>>>({
 *   routedFragments: [
 *     {
 *       type: 'component',
 *       path: 'create',
 *       loadComponent: () => import('./user-form.component').then(m => m.UserFormComponent),
 *       options: { closeOnBackdropClick: false },
 *     },
 *     {
 *       type: 'component',
 *       path: ':id',
 *       loadComponent: () => import('./user-form.component').then(m => m.UserFormComponent),
 *       options: { closeOnBackdropClick: false },
 *     },
 *   ],
 * })
 * ```
 */
@Injectable({ providedIn: 'root' })
export class RoutedModalService {
  private readonly fragmentService = inject(RoutedFragmentService);
  private readonly modalService = inject(ModalService);
  private readonly injector = inject(Injector);

  private readonly openModals = new Map<string, ModalRef>();

  constructor() {
    this.fragmentService
      .getParsedFragments('component')
      .subscribe(async (fragments) => {
        const currentFragments = new Set(fragments.map((f) => f.fragment));

        // Open modals for newly matched fragments
        for (const parsed of fragments) {
          if (!this.openModals.has(parsed.fragment)) {
            const route = parsed.route as ComponentRoutedFragment<ModalOptions>;
            const component = (await route.loadComponent()) as Type<unknown>;

            const ref = runInInjectionContext(this.injector, () =>
              this.modalService.openModal(
                component,
                parsed.params as Record<string, unknown>,
                route.options ?? {}
              )
            );

            this.openModals.set(parsed.fragment, ref);

            // When the modal is closed from inside (X button / navigateBack),
            // remove the fragment so the URL stays consistent.
            ref.afterClosed().then(() => {
              if (this.openModals.has(parsed.fragment)) {
                this.openModals.delete(parsed.fragment);
                this.fragmentService.removeFragmentPart(parsed.fragment);
              }
            });
          }
        }

        // Close modals whose fragments were removed from the URL
        for (const [fragment, ref] of this.openModals) {
          if (!currentFragments.has(fragment)) {
            ref.close();
            this.openModals.delete(fragment);
          }
        }
      });
  }
}
