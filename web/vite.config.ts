import path from 'node:path';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';


const endpoint = process.env.LAPLACE_API_URL ?? 'http://localhost:5187';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@ui': path.resolve(__dirname, 'src/ui'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/v1': endpoint,
      '/chess': endpoint,
      '/health': endpoint,
      '/openapi': endpoint,
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
