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
    // One Matrix4 prototype across R3F + force-graph (avoids determinantAffine crashes).
    dedupe: ['three'],
  },
  optimizeDeps: {
    include: ['three', 'react-force-graph-3d', 'react-force-graph-2d'],
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
    // three.js alone minifies to ~698 kB (gzip 180 kB) and is a single package —
    // it cannot be split below Vite's 500 kB default. Every first-party chunk is
    // well under 200 kB (see manualChunks below); budget sized to three + margin.
    chunkSizeWarningLimit: 750,
    rollupOptions: {
      output: {
        // Split the heavyweight visualization stacks out of the app chunk so no
        // chunk crosses Vite's 500 kB budget (the app chunk was 1.49 MB monolithic).
        manualChunks: {
          three: ['three'],
          r3f: ['@react-three/fiber', '@react-three/drei'],
          'force-graph': ['react-force-graph-2d', 'react-force-graph-3d'],
          react: ['react', 'react-dom', 'react-router-dom'],
        },
      },
    },
  },
});
