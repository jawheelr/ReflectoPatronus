import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Dev: `npm run dev` proxies the API so the browser sees one origin (no CORS). Prod: the API serves the built files.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:8080' } },
  build: { outDir: 'dist', sourcemap: false },
})
