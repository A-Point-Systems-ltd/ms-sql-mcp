import { ChildProcessWithoutNullStreams, spawn } from 'child_process';
import { McpClient } from './mcpClient';
import { McpToolError, traceablePayload, unwrapToolResult } from './parse';
import { Logger } from '../logger';

const PROTOCOL_VERSION = '2024-11-05';
const REQUEST_TIMEOUT_MS = 60_000;

interface PendingRequest {
  method: string;
  startedAt: number;
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer: NodeJS.Timeout;
}

/**
 * A private instance of MssqlMcp.exe spoken to directly over newline-delimited
 * JSON-RPC 2.0 (stdio). Separate from the instance the agent uses; the caller
 * supplies the env (connection profiles, read-only flags).
 */
export class McpStdioClient implements McpClient {
  private child: ChildProcessWithoutNullStreams | undefined;
  private nextId = 1;
  private readonly pending = new Map<number, PendingRequest>();
  private stdoutBuffer = '';
  private initialized = false;
  private disposed = false;

  constructor(
    private readonly exePath: string,
    private readonly env: Record<string, string>,
    private readonly log: Logger,
    /** Called when the server dies on its own (not via dispose()). */
    private readonly onUnexpectedExit?: () => void,
  ) {}

  async initialize(): Promise<void> {
    if (this.initialized) {
      return;
    }
    this.spawnProcess();

    await this.request('initialize', {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: 'ms-sql-mcp-vscode', version: '1.0.0' },
    });
    this.notify('notifications/initialized');

    this.initialized = true;
    this.log.info('client', 'MCP handshake complete; ready.');
  }

  async callTool(name: string, args: Record<string, unknown> = {}): Promise<unknown> {
    this.log.debug('tool', `→ ${name}`);
    this.log.trace('tool', `${name} arguments`, args);
    const startedAt = Date.now();
    try {
      const result = await this.request('tools/call', { name, arguments: args });
      const payload = unwrapToolResult(result);
      this.log.debug('tool', `← ${name} ok (${Date.now() - startedAt} ms)`);
      this.log.trace('tool', `${name} result`, traceablePayload(name, payload));
      return payload;
    } catch (err) {
      this.log.error('tool', `← ${name} FAILED (${Date.now() - startedAt} ms)`, err);
      throw err;
    }
  }

  dispose(): void {
    this.disposed = true;
    this.failAll(new McpToolError('Client disposed.'));
    if (this.child && !this.child.killed) {
      this.log.debug('client', 'Terminating private server process.');
      this.child.kill();
    }
    this.child = undefined;
    this.initialized = false;
  }

  private spawnProcess(): void {
    this.log.info('client', `Spawning ${this.exePath}`);
    const child = spawn(this.exePath, [], { windowsHide: true, env: { ...process.env, ...this.env } });
    this.child = child;

    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => this.onStdout(chunk));
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk: string) => {
      const text = chunk.trimEnd();
      if (text.length > 0) {
        this.log.warn('server', text);
      }
    });
    child.on('exit', (code) => this.onExit(code));
    child.on('error', (err) => {
      this.log.error('client', 'Server process error', err);
      this.failAll(new McpToolError(`Server process error: ${err.message}`));
    });
  }

  private onStdout(chunk: string): void {
    this.stdoutBuffer += chunk;
    let newlineIndex: number;
    while ((newlineIndex = this.stdoutBuffer.indexOf('\n')) >= 0) {
      const line = this.stdoutBuffer.slice(0, newlineIndex).trim();
      this.stdoutBuffer = this.stdoutBuffer.slice(newlineIndex + 1);
      if (line.length > 0) {
        this.handleLine(line);
      }
    }
  }

  private handleLine(line: string): void {
    let message: { id?: unknown; result?: unknown; error?: { message?: string; code?: number } };
    try {
      message = JSON.parse(line);
    } catch {
      // The server must keep stdout clean; surface anything else as a warning.
      this.log.warn('server', `Ignoring non-JSON stdout: ${line}`);
      return;
    }
    if (typeof message.id !== 'number') {
      this.log.trace('rpc', 'Notification or unmatched message', message);
      return;
    }
    const pending = this.pending.get(message.id);
    if (!pending) {
      this.log.warn('rpc', `Response for unknown request id ${message.id}`);
      return;
    }
    this.pending.delete(message.id);
    clearTimeout(pending.timer);
    if (message.error) {
      pending.reject(
        new McpToolError(
          `${message.error.message ?? 'JSON-RPC error'}${
            message.error.code !== undefined ? ` (code ${message.error.code})` : ''
          }`,
        ),
      );
    } else {
      pending.resolve(message.result);
    }
  }

  private onExit(code: number | null): void {
    const unexpected = !this.disposed;
    if (unexpected) {
      this.log.warn('client', `Server process exited unexpectedly (code ${code ?? 'null'}).`);
    }
    this.failAll(new McpToolError(`Server process exited (code ${code ?? 'null'}).`));
    this.child = undefined;
    this.initialized = false;
    if (unexpected) {
      this.onUnexpectedExit?.();
    }
  }

  private failAll(error: Error): void {
    for (const [, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
  }

  private request(method: string, params: Record<string, unknown>): Promise<unknown> {
    if (!this.child) {
      return Promise.reject(new McpToolError('Server process is not running.'));
    }
    const id = this.nextId++;
    const payload = JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n';
    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        this.log.error('rpc', `Request #${id} (${method}) timed out after ${REQUEST_TIMEOUT_MS} ms.`);
        reject(new McpToolError(`Timed out after ${REQUEST_TIMEOUT_MS} ms calling ${method}.`));
      }, REQUEST_TIMEOUT_MS);
      this.pending.set(id, { method, startedAt: Date.now(), resolve, reject, timer });
      this.child!.stdin.write(payload, (err) => {
        if (err) {
          this.pending.delete(id);
          clearTimeout(timer);
          this.log.error('rpc', `Failed to write request #${id} (${method})`, err);
          reject(new McpToolError(`Failed to write to server: ${err.message}`));
        }
      });
    });
  }

  private notify(method: string): void {
    if (!this.child) {
      return;
    }
    this.log.trace('rpc', `→ notification ${method}`);
    this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params: {} }) + '\n');
  }
}
