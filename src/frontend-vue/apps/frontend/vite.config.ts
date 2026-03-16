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
      '/realms': {
        target: 'http://localhost',
        changeOrigin: true,
        secure: false,
        cookieDomainRewrite: 'localhost',
        // Only proxy API/connect requests, serve SPA for realm navigation
        bypass(req) {
          if (req.url && !/\/realms\/[^/]+\/(api|connect|\.well-known)/.test(req.url)) {
            return '/index.html';
          }
        },
      },
      '/api': {
        target: 'http://localhost',
        changeOrigin: true,
        secure: false,
        cookieDomainRewrite: 'localhost',
      },
      '/connect': {
        target: 'http://localhost',
        changeOrigin: true,
        secure: false,
        cookieDomainRewrite: 'localhost',
      },
    },
  },
  resolve: {
    alias: {
      '@': '/src',
    },
  },
});
