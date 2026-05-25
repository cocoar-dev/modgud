import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// During `pnpm dev` the SPA runs on port 5173 and proxies /bff and /api
// to the BFF. In production the BFF serves the built SPA from its own
// wwwroot, so the proxy is dev-only.
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/bff': 'http://localhost:7080',
      '/api': 'http://localhost:7080',
      '/signin-oidc': 'http://localhost:7080',
      '/signout-callback-oidc': 'http://localhost:7080',
    },
  },
  build: {
    // Output directly into the BFF's wwwroot so `dotnet run` ships the
    // SPA shell. Adjust if you'd rather copy as a separate step.
    outDir: '../../dotnet/TestApps/Modgud.TestApps.Bff/wwwroot',
    emptyOutDir: true,
  },
})
