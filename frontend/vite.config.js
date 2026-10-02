import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
const api = 'http://localhost:5088';
export default defineConfig({
    plugins: [react(), tailwindcss()],
    server: {
        port: 5190,
        strictPort: true,
        proxy: {
            '/api': { target: api },
            '/connect': { target: api },
            '/.well-known': { target: api },
            '/mcp': { target: api },
        },
    },
});
