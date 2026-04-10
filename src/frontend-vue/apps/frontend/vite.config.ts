import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/postcss';

export default defineConfig({
  plugins: [vue()],
  css: {
    postcss: {
      plugins: [tailwindcss()],
    },
  },
  server: {
    port: 4200,
    proxy: {
      '/api': {
        target: 'http://localhost',
        changeOrigin: false,
        secure: false,
      },
      '/connect': {
        target: 'http://localhost',
        changeOrigin: false,
        secure: false,
      },
      '/.well-known': {
        target: 'http://localhost',
        changeOrigin: false,
        secure: false,
      },
      '/admin-hub': {
        target: 'http://localhost',
        changeOrigin: false,
        secure: false,
        ws: true,
      },
      '/health': {
        target: 'http://localhost',
        changeOrigin: false,
        secure: false,
      },
    },
  },
  resolve: {
    alias: {
      '@': '/src',
    },
  },
});
