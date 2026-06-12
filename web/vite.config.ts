import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Dev proxy targets the endpoint started by scripts\win\serve.cmd (port 5187).
const endpoint = process.env.LAPLACE_API_URL ?? 'http://localhost:5187';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/v1': endpoint,
      '/health': endpoint,
      '/openapi': endpoint,
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
