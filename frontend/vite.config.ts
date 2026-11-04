import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Сборка (frontend/dist) встраивается в Tasker.Web как ресурс,
// поэтому один и тот же фронт отдают и Tasker.Server, и локальный хост.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // Tasker.Server.
      '/api': 'http://localhost:5156',
      // Локальный хост на постоянном порту 5720.
      '^/w/[^/]+/api': 'http://127.0.0.1:5720',
    },
  },
})
