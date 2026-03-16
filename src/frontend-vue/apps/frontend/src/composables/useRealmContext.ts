/**
 * Detects the current realm from the URL at app startup.
 * Immutable — values never change during SPA lifetime.
 *
 * /realms/acme/... → slug='acme', apiUrl='/realms/acme/api', baseHref='/realms/acme/'
 * anything else    → slug='system', apiUrl='/api', baseHref='/'
 */

const match = window.location.pathname.match(/^\/realms\/([a-z][a-z0-9-]+)(\/|$)/);

export const realmContext = match
  ? { slug: match[1], apiUrl: `/realms/${match[1]}/api`, baseHref: `/realms/${match[1]}/`, isSystem: false }
  : { slug: 'system', apiUrl: '/api', baseHref: '/', isSystem: true };
