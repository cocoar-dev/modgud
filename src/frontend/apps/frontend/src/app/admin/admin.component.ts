import { Component, inject, DestroyRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router, ChildActivationStart } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { CoarMenuComponent, CoarMenuHeadingComponent, CoarMenuItemComponent, CoarSidebarComponent } from '@cocoar/ui';
import { UIService } from '../ui';

@Component({
  selector: 'app-auth-layout',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarSidebarComponent,
    CoarMenuComponent,
    CoarMenuItemComponent,
    CoarMenuHeadingComponent,
  ],
  templateUrl: './admin.component.html',
  styleUrl: './admin.component.css',
})
export class AdminComponent {
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  public readonly ui = inject(UIService);

  constructor() {
    // Reset UI state on navigation
    this.router.events
      .pipe(
        filter((event) => event instanceof ChildActivationStart),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe(() => {
        this.ui.reset();
      });
  }
}
