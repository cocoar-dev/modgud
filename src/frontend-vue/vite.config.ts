import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

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
      '@cocoar/vue-data-grid/styles': fileURLToPath(new URL('./node_modules/@cocoar/vue-data-grid/dist/index.css', import.meta.url)),
    },
  },
  server: {
    port: 4300,
    proxy: {
      '/api': {
        target: 'http://localhost:9099',
        changeOrigin: true,
      },
      '/signalr': {
        target: 'http://localhost:9099',
        changeOrigin: true,
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
    },
  },
})
