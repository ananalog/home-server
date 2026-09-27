import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The build goes straight into the server's wwwroot (served by ASP.NET Core).
export default defineConfig({
  plugins: [svelte()],
  base: './',
  build: {
    outDir: '../../src/Home.Server/wwwroot',
    emptyOutDir: true,
    target: 'es2020',
    chunkSizeWarningLimit: 300,
  },
  server: {
    port: 5173,
    host: true,
    proxy: {
      '/api': { target: process.env.HOME_API ?? 'http://127.0.0.1:8080', changeOrigin: true },
    },
  },
});
