import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    port: Number(process.env.PORT) || 5173,
  },
  // duckdb-wasm ships prebuilt esm; don't let Vite try to pre-bundle its worker entry points.
  optimizeDeps: { exclude: ['@duckdb/duckdb-wasm'] },
})
