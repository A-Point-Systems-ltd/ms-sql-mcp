import * as fs from 'fs';
import * as path from 'path';
import { McpEntry, mergeMcpServer } from './jsonMerge';
import { SERVER_KEY } from './naming';

// After an extension upgrade, points the client entries written by "Register with Cursor / Claude…" at the new
// version's exe copy and deletes the old copies. No 'vscode' import: the host is injected (unit tests drive it).

const EXE_FILE = 'mssqlmcp.exe';

/**
 * The version of the exe copy `command` runs when it is an older copy made by {@link copyStableExe}
 * (`<storageDir>\bin\<version>\MssqlMcp.exe`, compared case-insensitively); undefined for the current version or any
 * other path (a custom serverPath, a release folder), which is never touched.
 */
export function staleVersion(command: unknown, storageDir: string, currentVersion: string): string | undefined {
  const version = copiedVersion(command, storageDir);
  return version !== undefined && version !== currentVersion ? version : undefined;
}

/** The version folder of an exe copy under `<storageDir>\bin\`, or undefined for any other path. */
export function copiedVersion(command: unknown, storageDir: string): string | undefined {
  if (typeof command !== 'string' || !command.trim()) return undefined;
  const prefix = path.win32.join(path.win32.normalize(storageDir), 'bin').toLowerCase() + '\\';
  const normalized = path.win32.normalize(command.trim().replace(/^"|"$/g, ''));
  if (!normalized.toLowerCase().startsWith(prefix)) return undefined;
  const parts = normalized.slice(prefix.length).split('\\');
  if (parts.length !== 2 || parts[1].toLowerCase() !== EXE_FILE || !/^[0-9A-Za-z.+-]+$/.test(parts[0])) return undefined;
  return parts[0];
}

/** The `mcpServers[key]` entry of a client config, or undefined (no file text, invalid JSON, no such entry). */
export function readEntry(text: string | undefined, key: string = SERVER_KEY): Record<string, unknown> | undefined {
  if (!text?.trim()) return undefined;
  try {
    const servers = (JSON.parse(text.replace(/^﻿/, '')) as { mcpServers?: unknown } | null)?.mcpServers;
    if (!servers || typeof servers !== 'object' || Array.isArray(servers)) return undefined;
    const entry = (servers as Record<string, unknown>)[key];
    return entry && typeof entry === 'object' && !Array.isArray(entry) ? entry as Record<string, unknown> : undefined;
  } catch {
    return undefined;
  }
}

/**
 * The config text with only `mcpServers[key].command` replaced by `newCommand` (args, env and every other server
 * kept), or undefined when the entry is missing or `isStale` says its command needs no change.
 */
export function withNewCommand(text: string | undefined, newCommand: string, isStale: (command: unknown) => boolean, key: string = SERVER_KEY): string | undefined {
  const entry = readEntry(text, key);
  if (!entry || !isStale(entry.command)) return undefined;
  return mergeMcpServer(text, key, { ...entry, command: newCommand } as McpEntry);
}

/** Version folders under bin\ that may be deleted: not the current version and no client entry still uses them. */
export function removableVersions(folders: readonly string[], currentVersion: string, stillUsed: ReadonlySet<string>): string[] {
  return folders.filter(v => v !== currentVersion && !stillUsed.has(v) && /^[0-9A-Za-z.+-]+$/.test(v));
}

export interface UpgradeLog {
  info(scope: string, message: string): void;
  warn(scope: string, message: string): void;
  error(scope: string, message: string, err?: unknown): void;
}

export interface UpgradeDeps {
  /** The extension's global storage folder (holds bin\<version>\). */
  storageDir: string;
  version: string;
  /** JSON client configs that may hold the entry (Cursor, Claude Desktop and its MSIX copy). */
  configFiles: readonly { label: string; path: string }[];
  /** Copies the current exe to bin\<version>\ and returns its path. */
  ensureExe: () => Promise<string>;
  /** Writes a config (with a timestamped backup), as "Register with…" does. */
  writeConfig: (configPath: string, text: string) => void;
  /** Claude Code's user-scope entry: read it, and re-register it through the claude CLI. */
  claudeCode?: { read: () => Record<string, unknown> | undefined; register: (entry: McpEntry) => Promise<boolean> };
  log: UpgradeLog;
  fsApi?: Pick<typeof fs, 'existsSync' | 'readFileSync' | 'readdirSync' | 'rmSync'>;
}

const SCOPE = 'upgrade';

/**
 * Moves every registered client entry that runs an older exe copy to the current version, then deletes the copies
 * nothing uses any more (a copy still running is locked and is retried on the next activation). Returns the labels of
 * the clients that were updated. Never throws; never logs env values (they can hold passwords).
 */
export async function upgradeRegistrations(deps: UpgradeDeps): Promise<string[]> {
  const fsApi = deps.fsApi ?? fs;
  const isStale = (command: unknown) => staleVersion(command, deps.storageDir, deps.version) !== undefined;
  const updated: string[] = [];
  const stillUsed = new Set<string>();
  const use = (command: unknown) => {
    const v = copiedVersion(command, deps.storageDir);
    if (v) stillUsed.add(v);
  };
  let exe: string | undefined;
  const currentExe = async () => (exe ??= await deps.ensureExe());

  for (const file of deps.configFiles) {
    let text: string | undefined;
    try {
      text = fsApi.existsSync(file.path) ? String(fsApi.readFileSync(file.path, 'utf8')) : undefined;
      const entry = readEntry(text);
      if (!entry) continue;
      if (!isStale(entry.command)) { use(entry.command); continue; }
      const next = withNewCommand(text, await currentExe(), isStale);
      if (next) {
        deps.writeConfig(file.path, next);
        updated.push(file.label);
        deps.log.info(SCOPE, `${file.label}: ${file.path} now runs version ${deps.version}.`);
      }
    } catch (err) {
      use(readEntry(text)?.command);
      deps.log.error(SCOPE, `${file.label}: updating ${file.path} failed`, err);
    }
  }

  if (deps.claudeCode) {
    let entry: Record<string, unknown> | undefined;
    try {
      entry = deps.claudeCode.read();
      if (entry && isStale(entry.command)) {
        const { command: _old, type: _type, ...rest } = entry;
        if (await deps.claudeCode.register({ ...rest, command: await currentExe() } as McpEntry)) {
          updated.push('Claude Code');
          deps.log.info(SCOPE, `Claude Code: re-registered with version ${deps.version}.`);
        } else {
          use(entry.command);
          deps.log.warn(SCOPE, 'Claude Code: re-registering with the new version failed; run "Register with Cursor / Claude…".');
        }
      } else if (entry) {
        use(entry.command);
      }
    } catch (err) {
      use(entry?.command);
      deps.log.error(SCOPE, 'Claude Code: updating the registration failed', err);
    }
  }

  const bin = path.join(deps.storageDir, 'bin');
  let folders: string[] = [];
  try { folders = fsApi.existsSync(bin) ? (fsApi.readdirSync(bin) as string[]) : []; } catch { /* nothing to clean */ }
  for (const version of removableVersions(folders, deps.version, stillUsed)) {
    try {
      fsApi.rmSync(path.join(bin, version), { recursive: true, force: true, maxRetries: 2 });
      deps.log.info(SCOPE, `Deleted the old exe copy of version ${version}.`);
    } catch (err) {
      // A client still running the old exe locks it: try again on the next activation.
      deps.log.warn(SCOPE, `The old exe copy of version ${version} is in use; it is deleted on a later start (${(err as NodeJS.ErrnoException).code ?? String(err)}).`);
    }
  }
  return updated;
}
