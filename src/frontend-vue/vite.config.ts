import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  // The PageBuilder ships its SES worker as a Vite `?worker&url` import.
  // Rolldown's Windows dependency optimizer treats the query as part of the
  // filename, so let Vite transform this package as source instead of
  // pre-bundling it. Production builds are unaffected.
  optimizeDeps: {
    exclude: ['@cocoar/vue-page-builder'],
    // Its Temporal polyfill is still a regular dependency and needs
    // pre-bundling so the CommonJS `jsbi` dependency gets ESM interop.
    include: ['@cocoar/vue-page-builder > @js-temporal/polyfill'],
  },
  build: {
    target: 'esnext',
  },
  plugins: [
    vue(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
      '@cocoar/vue-data-grid/styles': fileURLToPath(new URL('./node_modules/@cocoar/vue-data-grid/dist/index.css', import.meta.url)),
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
