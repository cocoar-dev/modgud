import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { signalState, patchState } from '@ngrx/signals';
import { Observable, finalize } from 'rxjs';
import { deepClone } from './utils/deep-clone';
import { UIContext, UIContextInfo, UiContextDefaultState } from './ui.context';

@Injectable({ providedIn: 'root' })
export class UIService {
  private readonly router = inject(Router);

  public state = signalState<UIContext>(deepClone(UiContextDefaultState));

  protected uiContextInfo: UIContextInfo = { type: 'page' };

  /**
   * Reset the UI state to defaults.
   * Called automatically on navigation events.
   */
  public reset(): void {
    patchState(this.state, deepClone(UiContextDefaultState));
  }

  /**
   * Update the UI state using a callback function.
   * The callback receives a mutable copy of the current state and context info.
   *
   * @example
   * this.ui.set((ctx, info) => {
   *   ctx.header.title = 'My Page Title';
   *   ctx.header.subTitle = 'Subtitle';
   *   ctx.content.showLoadingBar = true;
   * });
   */
  public set(fn: (current: UIContext, info: UIContextInfo) => void): void {
    const current = deepClone({
      header: this.state.header(),
      content: this.state.content(),
      footer: this.state.footer(),
    });
    fn(current, this.uiContextInfo);
    patchState(this.state, current);
  }

  /**
   * Wrap an observable to show/hide the loading bar automatically.
   *
   * @example
   * this.ui.wrapWithLoadingBar(this.http.get('/api/data')).subscribe(data => { ... });
   */
  public wrapWithLoadingBar<T>(obs$: Observable<T>): Observable<T> {
    this.set((ctx) => {
      ctx.content.showLoadingBar = true;
    });

    return obs$.pipe(
      finalize(() => {
        this.set((ctx) => {
          ctx.content.showLoadingBar = false;
        });
      })
    );
  }

  /**
   * Navigate back in history or close the modal (when used in modal context).
   * Override in modal context to close the modal instead.
   */
  public navigateBack(): void {
    this.router.navigate(['..']);
  }
}
