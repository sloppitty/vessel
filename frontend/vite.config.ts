import { readFileSync } from 'node:fs'
import { fileURLToPath, URL } from 'node:url'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { loadEnv } from 'vite'
import { defineConfig } from 'vitest/config'

export default defineConfig(({ command, mode }) => {
  // Server-only target. Never pass credentials through Vite environment variables.
  const target = loadEnv(mode, process.cwd(), '').VESSEL_API_TARGET ?? 'http://127.0.0.1:5080'
  // The version handshake: a build states its package version; the dev server a placeholder that never shows the
  // reload notice. Tests leave it undefined and stub the global.
  const version = command === 'build'
    ? (JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8')) as { version: string }).version
    : 'development'
  return {
    plugins: [react(), tailwindcss()],
    ...(mode === 'test' ? {} : { define: { __VESSEL_VERSION__: JSON.stringify(version) } }),
    resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
    build: {
      rolldownOptions: {
        output: {
          codeSplitting: {
            groups: [
              { name: 'react', test: /[\\/]node_modules[\\/](react|react-dom|scheduler)[\\/]/, priority: 2 },
            ],
          },
        },
      },
    },
    server: {
      port: 5180,
      strictPort: true,
      proxy: { '/api': { target }, '/health': { target } },
    },
    test: {
      environment: 'jsdom',
      setupFiles: ['./src/test/setup.ts'],
      restoreMocks: true,
      clearMocks: true,
      // Shared CI runners are slower than local machines; interaction-heavy tests need headroom.
      testTimeout: 15_000,
    },
  }
})
