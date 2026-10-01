import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpStdioClient } from '../out/client/mcpStdioClient.js';
import { McpToolError } from '../out/client/parse.js';

const FAKE_SERVER = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'fakeServer.mjs');

/** Logger stand-in that keeps every line; the fake server's stderr arrives via warn('server', ...). */
function fakeLog() {
  const lines = [];
  const add = level => (scope, message) => lines.push({ level, scope, message });
  return { lines, error: add('error'), warn: add('warn'), info: add('info'), debug: add('debug'), trace: add('trace') };
}

/** Notifications the fake server reported on stderr. */
function notifications(log) {
  return log.lines
    .filter(l => l.scope === 'server')
    .flatMap(l => l.message.split(/\r?\n/))
    .filter(t => t.startsWith('NOTIFY '))
    .map(t => JSON.parse(t.slice('NOTIFY '.length)));
}

async function waitFor(predicate, ms = 3000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (predicate()) return true;
    await new Promise(r => setTimeout(r, 20));
  }
  return predicate();
}

const clients = [];
async function startClient(options) {
  const log = fakeLog();
  const client = new McpStdioClient(process.execPath, {}, log, undefined, { args: [FAKE_SERVER], ...options });
  clients.push(client);
  await client.initialize();
  return { client, log };
}

after(() => {
  for (const c of clients) c.dispose();
});

test('abort rejects at once with a cancelled McpToolError and sends notifications/cancelled with the requestId', async () => {
  const { client, log } = await startClient();
  const ac = new AbortController();
  const started = Date.now();
  const pending = client.callTool('slow', { ms: 800 }, { signal: ac.signal });
  setTimeout(() => ac.abort(), 50);
  await assert.rejects(pending, err => {
    assert.ok(err instanceof McpToolError);
    assert.equal(err.cancelled, true);
    assert.equal(err.message, 'Cancelled.');
    return true;
  });
  assert.ok(Date.now() - started < 600, 'rejected before the server answered');

  assert.ok(await waitFor(() => notifications(log).some(n => n.method === 'notifications/cancelled')), 'server received the cancellation');
  const cancel = notifications(log).find(n => n.method === 'notifications/cancelled');
  // initialize is request #1, so the slow call is #2.
  assert.deepEqual(cancel.params, { requestId: 2, reason: 'Cancelled by user' });

  // The late response is ignored quietly and the client keeps working.
  await new Promise(r => setTimeout(r, 900));
  assert.ok(!log.lines.some(l => /unknown request id/i.test(l.message)), 'late response of a cancelled request is not reported as unknown');
  assert.deepEqual(await client.callTool('echo', { x: 1 }), { success: true, data: { x: 1 } });
});

test('an already-aborted signal rejects without sending the request', async () => {
  const { client, log } = await startClient();
  const ac = new AbortController();
  ac.abort();
  await assert.rejects(client.callTool('slow', { ms: 100 }, { signal: ac.signal }), err => err.cancelled === true);
  await new Promise(r => setTimeout(r, 200));
  assert.ok(!notifications(log).some(n => n.method === 'notifications/cancelled'));
});

test('defaultTimeoutMs applies by default; timeoutMs: null waits as long as needed', async () => {
  const { client } = await startClient({ defaultTimeoutMs: 200 });
  await assert.rejects(client.callTool('slow', { ms: 500 }), err => {
    assert.ok(err instanceof McpToolError);
    assert.match(err.message, /Timed out after 200 ms/);
    assert.notEqual(err.cancelled, true);
    return true;
  });
  const ok = await client.callTool('slow', { ms: 500 }, { timeoutMs: null });
  assert.deepEqual(ok, { success: true, data: { slept: 500 } });
  // An explicit timeout overrides the default as well.
  assert.deepEqual(await client.callTool('slow', { ms: 300 }, { timeoutMs: 2000 }), { success: true, data: { slept: 300 } });
});

test('notify sends a notification with empty params by default', async () => {
  const { client, log } = await startClient();
  client.notify('notifications/test');
  assert.ok(await waitFor(() => notifications(log).some(n => n.method === 'notifications/test')));
  assert.deepEqual(notifications(log).find(n => n.method === 'notifications/test').params, {});
});

test('dispose kills the server process', async () => {
  const { client } = await startClient();
  const child = client.child;
  assert.ok(child && child.exitCode === null);
  client.dispose();
  assert.ok(await waitFor(() => child.exitCode !== null || child.signalCode !== null), 'child exited');
});

test('an initialize timeout does not send notifications/cancelled', async () => {
  const log = fakeLog();
  const client = new McpStdioClient(process.execPath, { FAKE_INIT_DELAY_MS: '400' }, log, undefined, { args: [FAKE_SERVER], defaultTimeoutMs: 150 });
  clients.push(client);
  await assert.rejects(client.initialize(), /Timed out after 150 ms calling initialize/);
  // Give a wrongly sent notification time to reach the server and the late initialize response time to arrive.
  client.notify('notifications/probe');
  assert.ok(await waitFor(() => notifications(log).some(n => n.method === 'notifications/probe')), 'the server is still reading stdin');
  await new Promise(r => setTimeout(r, 350));
  assert.ok(!notifications(log).some(n => n.method === 'notifications/cancelled'), 'initialize is never cancelled');
  assert.ok(!log.lines.some(l => /unknown request id/i.test(l.message)), 'the late initialize response is dropped quietly');
});
