// SPDX-License-Identifier: GPL-3.0-only
// Private JSON-lines worker. The desktop process owns its timeout and cancellation.
'use strict';
const readline = require('node:readline');
for (const method of ['log', 'info', 'warn', 'error', 'debug']) console[method] = () => {};
const write = value => new Promise((resolve, reject) => process.stdout.write(JSON.stringify(value) + '\n', error => error ? reject(error) : resolve()));
process.stdout.on('error', () => process.exit(0));
process.on('uncaughtException', () => { write({ error: 'po_worker_failed' }).finally(() => process.exit(1)); });
process.on('unhandledRejection', () => { write({ error: 'po_worker_failed' }).finally(() => process.exit(1)); });
(async () => {
  const { SessionManager } = require('./dist/provider.cjs');
  // Keep only the upstream integrity/minter cache. Per-video tokens stay in the host.
  const manager = new SessionManager(false);
  if (process.argv.includes('--check-runtime')) {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 2;
    const context = canvas.getContext('2d');
    if (!context) throw new Error('Canvas unavailable');
    context.fillRect(0, 0, 2, 2);
    if (!canvas.toDataURL().startsWith('data:image/png;base64,')) throw new Error('Canvas failed');
    await write({ ready: true });
    process.exit(0);
  }
  const lines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity, terminal: false });
  for await (const line of lines) {
    if (line.length > 4096) { await write({ error: 'invalid_request' }); continue; }
    let request;
    try { request = JSON.parse(line); } catch { await write({ error: 'invalid_request' }); continue; }
    if (!request || typeof request !== 'object' || Array.isArray(request)
      || Object.keys(request).some(key => key !== 'video_id')
      || typeof request.video_id !== 'string' || !/^[A-Za-z0-9_-]{11}$/.test(request.video_id)) {
      await write({ error: 'invalid_request' }); continue;
    }
    try {
      const result = await manager.generatePoToken(request.video_id);
      const token = result.poToken.replace(/=+$/, '');
      const now = Math.floor(Date.now() / 1000);
      const integrityExpiries = [...manager.minterCache.values()].map(value => Math.floor(value.expiry.getTime() / 1000));
      const expiresAt = Math.min(now + 3500, ...integrityExpiries);
      if (!/^[A-Za-z0-9_-]{32,8192}$/.test(token) || expiresAt <= now + 30)
        throw new Error('Invalid proof metadata');
      await write({ gvs_token: token, expires_at: expiresAt });
    } catch { await write({ error: 'po_generation_failed' }); }
  }
  manager.invalidateCaches();
  process.exit(0);
})().catch(() => { write({ error: 'po_worker_failed' }).finally(() => process.exit(1)); });
