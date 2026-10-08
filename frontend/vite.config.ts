import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Сборка (frontend/dist) встраивается в Tasker.Web как ресурс,
// поэтому один и тот же фронт отдают и Tasker.Server, и Tasker.Desktop.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // Tasker.Server.
      '/api': 'http://localhost:5156',
      // Tasker.Desktop (TASKER_FRONTEND_DEV_URL=http://localhost:5173): API рабочих областей — на постоянном порту MCP.
      '^/w/[^/]+/api': 'http://127.0.0.1:5719',
      '^/mcp': 'http://127.0.0.1:5719',
    },
  },
})
