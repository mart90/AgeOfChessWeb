import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  server: {
    proxy: {
      '/api': 'http://localhost:5000',
      '/hub': {
        target: 'http://localhost:5000',
        ws: true,
      },
    },
  },
  build: {
    outDir: '../AgeOfChess.Server/wwwroot',
    emptyOutDir: true,
  },
});
