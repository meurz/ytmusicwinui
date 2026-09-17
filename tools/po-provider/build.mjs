// SPDX-License-Identifier: GPL-3.0-only
import { build } from 'esbuild';
import { mkdir } from 'node:fs/promises';
await mkdir('dist', { recursive: true });
await build({
  entryPoints: ['vendor/bgutil/session_manager.ts'],
  outfile: 'dist/provider.cjs',
  platform: 'node',
  format: 'cjs',
  target: 'node22',
  bundle: true,
  external: ['jsdom', 'canvas'],
  legalComments: 'linked',
  logLevel: 'warning'
});
