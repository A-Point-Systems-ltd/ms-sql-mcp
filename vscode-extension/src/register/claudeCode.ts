import { exec, execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { McpEntry } from './jsonMerge';
import { SERVER_KEY } from './naming';

export interface ExecResult { code: number | 'ENOENT' | 'EINVAL' | 'ERROR'; stderr: string }
export type Runner = (file: string, args: string[], opts: { shell: boolean }) => Promise<ExecResult>;

/** Quote one argument for a cmd.exe command line (MSVCRT argv rules: escape quotes, double backslashes before them). */
export function cmdQuote(arg: string): string {
  const escaped = arg.replace(/(\\*)"/g, '$1$1\\"').replace(/(\\+)$/, '$1$1');
  return `"${escaped}"`;
}

/** cmd.exe still expands these inside double quotes or treats them as line breaks; such payloads are never sent through a shell. */
const CMD_UNSAFE = /[%^&|<>!\r\n\0]/;

export function addJsonArgs(entry: McpEntry): string[] {
  return ['mcp', 'add-json', '--scope', 'user', SERVER_KEY, JSON.stringify({ type: 'stdio', ...entry })];
}

/**
 * Text to paste into Windows PowerShell 5.1 (also fine in PowerShell 7). Windows PowerShell 5.1 splits or strips quotes
 * in a native-program argument that contains both spaces and quotes, so the JSON follows the stop-parsing token `--%`
 * and is passed raw as one double-quoted argument with inner quotes escaped as \" (verified against 5.1 in tests).
 * Limitation: `--%` still expands %VAR% sequences, so a path containing '%' would be altered.
 * `prefix` exists so tests can substitute another program for `claude`.
 */
export function powershellCommand(entry: McpEntry, prefix = 'claude'): string {
  const json = JSON.stringify({ type: 'stdio', ...entry }).replace(/(\\*)"/g, '$1$1\\"');
  return `${prefix} mcp add-json --scope user ${SERVER_KEY} --% "${json}"`;
}

/** Resolve the first `claude` launcher on PATH (honouring PATHEXT). */
export function findClaude(env: NodeJS.ProcessEnv = process.env, exists: (p: string) => boolean = fs.existsSync): string | undefined {
  const exts = (env.PATHEXT ?? '.COM;.EXE;.BAT;.CMD').split(';').filter(Boolean);
  for (const dir of (env.PATH ?? env.Path ?? '').split(path.delimiter).filter(Boolean)) {
    for (const ext of exts) {
      const candidate = path.join(dir.replace(/^"|"$/g, ''), `claude${ext.toLowerCase()}`);
      if (exists(candidate)) return candidate;
    }
  }
  return undefined;
}

export const defaultRunner: Runner = (file, args, opts) => new Promise(resolve => {
  const finish = (err: (Error & { code?: unknown }) | null, stderr: string) => {
    if (!err) return resolve({ code: 0, stderr });
    const c = err.code;
    if (c === 'ENOENT' || c === 'EINVAL') return resolve({ code: c, stderr });
    resolve({ code: typeof c === 'number' ? c : 'ERROR', stderr: stderr || err.message });
  };
  try {
    // The shell line is built here (args are already cmd-quoted) instead of passing args + shell:true (DEP0190).
    if (opts.shell) exec(`${cmdQuote(file)} ${args.join(' ')}`, { windowsHide: true, timeout: 30_000 }, (err, _o, stderr) => finish(err, String(stderr)));
    else execFile(file, args, { shell: false, windowsHide: true, timeout: 30_000 }, (err, _o, stderr) => finish(err, String(stderr)));
  } catch (err) {
    finish(err as Error, '');
  }
});

export type ClaudeCodeOutcome =
  | { status: 'registered' }
  | { status: 'unavailable'; reason: string }
  | { status: 'failed'; message: string };

const NOT_FOUND = (r: ExecResult) => r.code === 'ENOENT' || r.code === 'EINVAL' || r.code === 9009 || /is not recognized/i.test(r.stderr);

/**
 * Registers via `claude mcp add-json --scope user`. The launcher is located on PATH first (PATHEXT aware):
 * .exe/.com run with execFile and shell:false; a .cmd/.bat shim (which Node >= 18.20 refuses to spawn directly,
 * CVE-2024-27980) runs through cmd.exe with quoted arguments, and only when the payload has no cmd metacharacters.
 * Anything else is 'unavailable' so the caller can offer the command to copy.
 */
export async function registerClaudeCode(entry: McpEntry, run: Runner = defaultRunner, find: () => string | undefined = findClaude): Promise<ClaudeCodeOutcome> {
  const add = addJsonArgs(entry);
  const remove = ['mcp', 'remove', '--scope', 'user', SERVER_KEY];
  const file = find();
  if (!file) return { status: 'unavailable', reason: 'the claude CLI was not found on PATH' };
  const shell = /\.(cmd|bat)$/i.test(file);
  if (shell && CMD_UNSAFE.test(add[add.length - 1])) {
    return { status: 'unavailable', reason: 'claude.cmd cannot be started safely with these paths' };
  }
  const attempt = (args: string[]) => run(file, shell ? args.map(cmdQuote) : args, { shell });

  const probe = await attempt(remove);
  if (NOT_FOUND(probe)) return { status: 'unavailable', reason: 'the claude CLI could not be started' };
  // `remove` failing (server not registered yet) is expected and ignored.
  const res = await attempt(add);
  if (NOT_FOUND(res)) return { status: 'unavailable', reason: 'the claude CLI could not be started' };
  return res.code === 0 ? { status: 'registered' } : { status: 'failed', message: res.stderr.trim() || `claude exited with ${String(res.code)}` };
}
