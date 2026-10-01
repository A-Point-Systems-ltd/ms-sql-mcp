// Tiny fake MCP server for McpStdioClient tests: newline-delimited JSON-RPC over stdio.
// - `initialize` gets a result after FAKE_INIT_DELAY_MS (default 0) ms;
// - `tools/call` `slow` answers after `arguments.ms` (default 500) ms, even when cancelled (to exercise late responses);
// - `tools/call` `echo` answers at once with its arguments;
// - every notification received is written to stderr as `NOTIFY <json>`.
import { createInterface } from 'node:readline';

const send = msg => process.stdout.write(JSON.stringify(msg) + '\n');
const toolResult = payload => ({ content: [{ type: 'text', text: JSON.stringify(payload) }] });

const rl = createInterface({ input: process.stdin });
rl.on('line', line => {
  if (!line.trim()) return;
  const msg = JSON.parse(line);
  if (msg.id === undefined) {
    process.stderr.write(`NOTIFY ${JSON.stringify({ method: msg.method, params: msg.params })}\n`);
    return;
  }
  if (msg.method === 'initialize') {
    const delay = Number(process.env.FAKE_INIT_DELAY_MS ?? 0);
    const reply = () => send({ jsonrpc: '2.0', id: msg.id, result: { protocolVersion: '2024-11-05', capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '0' } } });
    if (delay > 0) setTimeout(reply, delay); else reply();
    return;
  }
  if (msg.method === 'tools/call') {
    const { name, arguments: args = {} } = msg.params ?? {};
    if (name === 'slow') {
      setTimeout(() => send({ jsonrpc: '2.0', id: msg.id, result: toolResult({ success: true, data: { slept: args.ms ?? 500 } }) }), args.ms ?? 500);
      return;
    }
    if (name === 'echo') {
      send({ jsonrpc: '2.0', id: msg.id, result: toolResult({ success: true, data: args }) });
      return;
    }
  }
  send({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: `Unknown method ${msg.method}` } });
});
rl.on('close', () => process.exit(0));
