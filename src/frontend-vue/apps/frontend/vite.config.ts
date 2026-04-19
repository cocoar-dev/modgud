import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';

// Backend RealmMiddleware resolves the tenant from the Host header. The system
// realm is registered under "system.localhost" only, so we rewrite Host on the
// proxied request — that way the backend sees a registered domain and routes
// the dev session into the system realm. No DB tweak required.
const PROXY_TARGET = 'http://localhost:5128';
const PROXY_HOST = 'system.localhost';

const backendProxy = {
  target: PROXY_TARGET,
  changeOrigin: false,
  secure: false,
  headers: {
    Host: PROXY_HOST,
  },
};

export default defineConfig({
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
    },
  },
  server: {
    port: 4200,
    proxy: {
      '/api': backendProxy,
      '/connect': backendProxy,
      '/.well-known': backendProxy,
      '/health': backendProxy,
      '/admin-hub': { ...backendProxy, ws: true },
    },
  },
});
