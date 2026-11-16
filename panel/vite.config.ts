import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Geliştirmede /api istekleri yerel API'ye (dotnet run, http://localhost:5080) yönlendirilir: aynı köken, çerez çalışır.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5080' }
  },
  build: { outDir: 'dist', sourcemap: false }
});
