import { defineConfig } from '@playwright/test';

// The Electron visual reference set (e2e/visual-reference.spec.ts, tools/visual/report.mjs); not part of the tests.
export default defineConfig({
  testDir: 'e2e',
  testMatch: 'visual-reference.spec.ts',
  timeout: 180_000,
  workers: 1,
  reporter: process.env.GITHUB_ACTIONS ? [['list'], ['github']] : 'list',
});
