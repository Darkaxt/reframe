import { resolve } from 'node:path';
import { defineConfig } from 'vitest/config';

// The PTY host conformance tests (docs/ptyhost-protocol.md): both hosts, driven over their pipes. They need the Node
// host built (`npm run build`) and the Rust host's executable (REFRAME_RUST_HOST_EXE).
export default defineConfig({
  resolve: { alias: { '@shared': resolve(__dirname, 'src/shared') } },
  test: {
    include: ['src/**/*.conformance.ts'],
    environment: 'node',
    testTimeout: 60_000,
    hookTimeout: 60_000,
    fileParallelism: false,
  },
});
