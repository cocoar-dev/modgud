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
      // Proxy realm API/connect/.well-known requests to backend
      // Regex: any path starting with /{slug}/(api|connect|.well-known)
      '^/[a-z][a-z0-9-]+/(api|connect|\\.well-known)': {
        target: 'http://localhost',
        changeOrigin: true,
        secure: false,
        cookieDomainRewrite: 'localhost',
      },
      // Global health endpoint
      '/health': {
        target: 'http://localhost',
        changeOrigin: true,
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
