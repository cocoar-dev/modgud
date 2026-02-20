import { Component, inject, Injector, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { CoarMenuComponent, CoarMenuHeadingComponent, CoarMenuItemComponent, CoarSidebarComponent } from '@cocoar/ui';
import { RoutedModalService, UIService } from '../ui';

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
export class AdminComponent implements OnInit {
  public readonly ui = inject(UIService);
  private readonly injector = inject(Injector);

  ngOnInit(): void {
    // Instantiate lazily — after the route is fully resolved so RoutedFragmentService
    // can safely read the ActivatedRoute snapshot without hitting undefined.
    this.injector.get(RoutedModalService);
  }
}
