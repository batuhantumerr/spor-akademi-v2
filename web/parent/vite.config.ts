/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { parentPwa } from './pwa/plugin.ts';

// Same-origin dev proxy to the local API (scripts/run-api.sh), like web/admin/proxy.conf.json: the refresh
// cookie is SameSite=Strict and CORS is off, so the browser must see a single origin.
const api = { target: 'http://127.0.0.1:5080', changeOrigin: false, secure: false };

export default defineConfig({
  // P3-07: manifest, icons and the service worker (DEC-394).
  plugins: [react(), tailwindcss(), parentPwa()],
  server: {
    port: 4201,
    strictPort: true,
    proxy: {
      '/api': api,
      '/health': api,
      // P3-08: the notifications hub (SignalR negotiate over HTTP, then a WebSocket).
      '/hubs': { ...api, ws: true },
    },
  },
  preview: {
    port: 4201,
    strictPort: true,
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}', 'sw/**/*.test.ts', 'pwa/**/*.test.ts'],
    restoreMocks: true,
  },
});
