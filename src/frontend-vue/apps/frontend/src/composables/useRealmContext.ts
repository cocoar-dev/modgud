/**
 * Detects the current realm from the URL at app startup.
 * Immutable — values never change during SPA lifetime.
 *
 * /acme/...   → slug='acme',   apiUrl='/acme/api',   baseHref='/acme/'
 * /system/... → slug='system', apiUrl='/system/api', baseHref='/system/'
 * anything else → falls back to slug='system'
 */

const match = window.location.pathname.match(/^\/([a-z][a-z0-9-]+)(\/|$)/);
const slug = match ? match[1] : 'system';

export const realmContext = {
  slug,
  apiUrl: `/${slug}/api`,
  baseHref: `/${slug}/`,
  isSystem: slug === 'system',
};
