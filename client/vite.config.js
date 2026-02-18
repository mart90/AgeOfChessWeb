import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  server: {
    proxy: {
      '/api': {
        target: 'https://localhost:7074',
        secure: false,
      },
      '/hub': {
        target: 'https://localhost:7074',
        ws: true,
        secure: false,
      },
    },
  },
  build: {
    outDir: '../AgeOfChess.Server/wwwroot',
    emptyOutDir: true,
  },
});
