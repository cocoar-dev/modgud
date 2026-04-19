import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';

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
      '/api': {
        target: 'http://localhost:5128',
        changeOrigin: false,
        secure: false,
      },
      '/connect': {
        target: 'http://localhost:5128',
        changeOrigin: false,
        secure: false,
      },
      '/.well-known': {
        target: 'http://localhost:5128',
        changeOrigin: false,
        secure: false,
      },
      '/admin-hub': {
        target: 'http://localhost:5128',
        changeOrigin: false,
        secure: false,
        ws: true,
      },
      '/health': {
        target: 'http://localhost:5128',
        changeOrigin: false,
        secure: false,
      },
    },
  },
});
