import { Injectable } from '@angular/core';

/**
 * Detects the current realm from the URL at construction time.
 * Immutable — values never change during SPA lifetime.
 *
 * /{slug}/... → slug='acme', apiUrl='/acme/api', baseHref='/acme/'
 * /system/... → slug='system', apiUrl='/system/api', baseHref='/system/'
 * bare "/"    → fallback to system (backend should redirect to /system/)
 */
@Injectable({
  providedIn: 'root',
})
export class RealmContextService {
  readonly slug: string;
  readonly apiUrl: string;
  readonly baseHref: string;
  readonly isSystem: boolean;

  constructor() {
    const match = window.location.pathname.match(/^\/([a-z][a-z0-9-]+)(\/|$)/);
    if (match) {
      this.slug = match[1];
      this.apiUrl = `/${this.slug}/api`;
      this.baseHref = `/${this.slug}/`;
      this.isSystem = this.slug === 'system';
    } else {
      // Fallback (bare "/" should redirect to /system/ by backend)
      this.slug = 'system';
      this.apiUrl = '/system/api';
      this.baseHref = '/system/';
      this.isSystem = true;
    }
  }
}
