import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '')
  const service = env.VITE_HSH_DEV_API_URL || 'http://127.0.0.1:5080'
  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': service,
        '/health': service,
        '/hubs': {
          target: service,
          ws: true,
        },
      },
    },
  }
})
