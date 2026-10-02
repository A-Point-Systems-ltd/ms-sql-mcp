import * as fs from 'fs';
import { ConnectionProfile } from './connections/profile';
import { AgentServerSettings, buildServerConnections, cursorServerEnv, missingPasswordMessage, missingPasswords } from './connections/serverEnv';
import { LEGACY_SERVER_KEY, SERVER_KEY } from './register/naming';

// This module must not import 'vscode': the host is injected so the unit tests can drive it with a fake.

/** Cursor's own MCP extension API (https://cursor.com/docs/context/mcp-extension-api), reached as `vscode.cursor.mcp`. */
export interface CursorMcpApi {
  registerServer(config: { name: string; server: { command: string; args: string[]; env: Record<string, string> } }): void;
  unregisterServer(name: string): void;
}

/** Returns `host.cursor.mcp` when both functions exist (Cursor only); detection is by the API's presence, never the app name. */
export function cursorMcpApi(host: unknown): CursorMcpApi | undefined {
  const mcp = (host as { cursor?: { mcp?: Partial<CursorMcpApi> } } | undefined)?.cursor?.mcp;
  if (mcp && typeof mcp.registerServer === 'function' && typeof mcp.unregisterServer === 'function') return mcp as CursorMcpApi;
  return undefined;
}

/** The subset of Logger the registrar uses. */
export interface RegistrarLog {
  info(scope: string, message: string): void;
  warn(scope: string, message: string): void;
  error(scope: string, message: string, err?: unknown): void;
}

export interface CursorRegistrarDeps {
  exePath: () => string | undefined;
  profiles: () => ConnectionProfile[];
  passwords: () => Promise<Map<string, string>>;
  settings: () => AgentServerSettings;
  log: RegistrarLog;
  warn: (message: string) => void;
}

const SCOPE = 'cursorMcp';

/**
 * Keeps the agent-facing APoint-ms-sql server registered with Cursor through `cursor.mcp.registerServer`, because Cursor
 * ignores `vscode.lm.registerMcpServerDefinitionProvider`. The registration carries the connection strings (with
 * passwords) in its env, so nothing about it except the connection count is ever logged.
 */
export class CursorMcpRegistrar {
  private registered = false;
  private lastKey: string | undefined;
  private lastMissing = '';
  private disposed = false;
  private current: Promise<void> | undefined;
  private queued: Promise<void> | undefined;

  constructor(private readonly api: CursorMcpApi, private readonly deps: CursorRegistrarDeps) {}

  /** Brings the registration in line with the current profiles and settings. At most one sync runs and one waits. */
  sync(): Promise<void> {
    if (this.disposed) return Promise.resolve();
    if (!this.current) return this.start();
    this.queued ??= this.current.then(() => {
      this.queued = undefined;
      return this.start();
    });
    return this.queued;
  }

  /** Unregisters the server; later syncs do nothing. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unregister();
  }

  private start(): Promise<void> {
    const run = this.run().finally(() => { if (this.current === run) this.current = undefined; });
    this.current = run;
    return run;
  }

  private async run(): Promise<void> {
    try {
      const exe = this.deps.exePath();
      const profiles = this.deps.profiles();
      const passwords = await this.deps.passwords();
      if (this.disposed) return;

      const missing = missingPasswords(profiles, passwords);
      const missingKey = missing.join('\n');
      if (missingKey !== this.lastMissing) {
        this.lastMissing = missingKey;
        if (missing.length) this.deps.warn(`APoint-ms-sql: ${missing.length} connection(s) skipped. ${missing.map(missingPasswordMessage).join(' ')}`);
      }

      // The server exits on an empty config, so register it only when at least one open profile is usable.
      const usable = profiles.filter(p => p.open).length - missing.length;
      if (!exe || usable <= 0) {
        this.unregister();
        return;
      }

      const server = { command: exe, args: [] as string[], env: cursorServerEnv(buildServerConnections(profiles, passwords), this.deps.settings()) };
      const key = JSON.stringify(server);
      if (this.registered && key === this.lastKey) return;

      this.unregister();
      this.api.registerServer({ name: SERVER_KEY, server });
      this.registered = true;
      this.lastKey = key;
      this.deps.log.info(SCOPE, `Registered '${SERVER_KEY}' with Cursor (${usable} connections)`);
    } catch (err) {
      this.deps.log.error(SCOPE, 'Registering the server with Cursor failed', err);
    }
  }

  private unregister(): void {
    if (!this.registered) return;
    this.registered = false;
    this.lastKey = undefined;
    try {
      this.api.unregisterServer(SERVER_KEY);
      this.deps.log.info(SCOPE, `Unregistered '${SERVER_KEY}' from Cursor`);
    } catch (err) {
      this.deps.log.error(SCOPE, 'Unregistering the server from Cursor failed', err);
    }
  }
}

/**
 * What activation does about a duplicate `APoint-ms-sql` or legacy `ms-sql` entry in ~/.cursor/mcp.json: warn while it exists unless the user
 * chose "Don't show again", and clear that choice once the entry is gone (so a new duplicate warns again).
 */
export function duplicateEntryAction(hasEntry: boolean, dismissed: boolean): 'warn' | 'clear' | 'none' {
  if (!hasEntry) return dismissed ? 'clear' : 'none';
  return dismissed ? 'none' : 'warn';
}

/**
 * True when a Cursor mcp.json already has an `APoint-ms-sql` or legacy `ms-sql` entry. Read-only: a missing file, invalid JSON or an unexpected
 * shape counts as "no entry" and is skipped silently.
 */
export function hasMsSqlEntry(configPath: string, fsApi: Pick<typeof fs, 'readFileSync'> = fs): boolean {
  try {
    const json: unknown = JSON.parse(fsApi.readFileSync(configPath, 'utf8').replace(/^\uFEFF/, ''));
    const servers = (json as { mcpServers?: unknown } | null)?.mcpServers;
    return typeof servers === 'object' && servers !== null
      && [SERVER_KEY, LEGACY_SERVER_KEY].some(k => Object.prototype.hasOwnProperty.call(servers, k));
  } catch {
    return false;
  }
}
