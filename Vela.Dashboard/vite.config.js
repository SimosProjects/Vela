import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Vela.Dashboard lives at the solution root alongside Vela.Api.
// `npm run build` drops the compiled bundle into Vela.Api/wwwroot.
// `npm run dev`   runs the Vite dev server on :5173 and proxies /api/* to
//                 the .NET API (adjust the target port if yours differs).

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Vela.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
});
