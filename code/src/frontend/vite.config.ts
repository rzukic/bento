import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    port: 5173,
    proxy: {
      "/api/v1": {
        target: "http://127.0.0.1:5000",
        changeOrigin: true
      },
      "/ws": {
        target: "ws://127.0.0.1:5000",
        ws: true
      },
      // MCP and its OAuth discovery (Step 4.6); /oauth/authorize stays in the SPA.
      "^/mcp$": {
        target: "http://127.0.0.1:5000"
      },
      "/.well-known": {
        target: "http://127.0.0.1:5000"
      }
    }
  },
  test: {
    environment: "jsdom",
    setupFiles: "./vitest.setup.ts",
    css: true,
    // Generous for the longest flows under parallel load; waits for elements are in vitest.setup.ts.
    testTimeout: 20_000
  }
});
