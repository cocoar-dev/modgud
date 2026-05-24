/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  build: {
    target: 'esnext',
  },
  // Vitest only scans the Vue source tree. The e2e/ directory is
  // Playwright — its *.spec.ts files import from @playwright/test
  // and crash on `test.describe()` if vitest tries to run them.
  test: {
    include: ['src/**/*.{test,spec}.{js,ts,jsx,tsx}'],
    exclude: ['node_modules', 'dist', 'e2e'],
    // No frontend unit tests exist yet (everything goes through e2e);
    // don't fail CI just because the suite is empty.
    passWithNoTests: true,
  },
  plugins: [
    vue(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
      '@cocoar/vue-data-grid/styles': fileURLToPath(new URL('./node_modules/@cocoar/vue-data-grid/dist/index.css', import.meta.url)),
      // @cocoar/vue-page-builder 2.1.0 ships dist/index.css but its
      // package.json exports map only exposes `.` (JS only). Alias
      // matches the data-grid pattern so we can @import "/styles" from
      // the consumer side as if the export were canonical.
      '@cocoar/vue-page-builder/styles': fileURLToPath(new URL('./node_modules/@cocoar/vue-page-builder/dist/index.css', import.meta.url)),
    },
  },
  server: {
    port: 4300,
    proxy: {
      // changeOrigin: false — keep the browser's original Host header (e.g.
      // `acme.localhost:4300`) when proxying. Required for multi-realm dev
      // testing: RealmMiddleware on the backend resolves the tenant by
      // Host. With changeOrigin:true, every dev request would resolve to
      // the system realm regardless of which tenant subdomain the browser
      // points at, defeating the whole point of C14.
      '/api': {
        target: 'http://localhost:9099',
        changeOrigin: false,
      },
      '/signalr': {
        target: 'http://localhost:9099',
        changeOrigin: false,
        ws: true,
      },
      // OIDC callbacks land outside /api (the path is what's registered with
      // the IdP as redirect_uri). Proxy them to the backend so dev-mode
      // browser redirects reach the auth handlers.
      '/signin-oidc': {
        target: 'http://localhost:9099',
        changeOrigin: true,
      },
      '/signout-callback-oidc': {
        target: 'http://localhost:9099',
        changeOrigin: true,
      },
      // OpenIddict OAuth endpoints (/connect/authorize, /connect/token,
      // /connect/consent, /connect/userinfo, …). Keep changeOrigin:false
      // so Host header survives — same realm-resolution reason as /api.
      '/connect': {
        target: 'http://localhost:9099',
        changeOrigin: false,
      },
      // Discovery + JWKS — well-known endpoints served by OpenIddict.
      '/.well-known': {
        target: 'http://localhost:9099',
        changeOrigin: false,
      },
    },
  },
})
