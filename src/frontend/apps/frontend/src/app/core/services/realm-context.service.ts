import { Injectable } from '@angular/core';

/**
 * Detects the current realm from the URL at construction time.
 * Immutable — values never change during SPA lifetime.
 *
 * /realms/acme/... → slug='acme', apiUrl='/realms/acme/api', baseHref='/realms/acme/'
 * anything else    → slug='system', apiUrl='/api', baseHref='/'
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
    const match = window.location.pathname.match(/^\/realms\/([a-z][a-z0-9-]+)\//);
    if (match) {
      this.slug = match[1];
      this.apiUrl = `/realms/${this.slug}/api`;
      this.baseHref = `/realms/${this.slug}/`;
      this.isSystem = false;
    } else {
      this.slug = 'system';
      this.apiUrl = '/api';
      this.baseHref = '/';
      this.isSystem = true;
    }
  }
}
