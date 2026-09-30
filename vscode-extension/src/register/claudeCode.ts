import { execFile } from 'child_process';
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

/** Command the user can paste into a bash or PowerShell terminal when the CLI cannot be run from the extension. */
export function manualCommand(entry: McpEntry): string {
  return `claude mcp add-json --scope user ${SERVER_KEY} '${JSON.stringify({ type: 'stdio', ...entry }).replace(/'/g, "''")}'`;
}

export const defaultRunner: Runner = (file, args, opts) => new Promise(resolve => {
  const finish = (err: (Error & { code?: unknown }) | null, stderr: string) => {
    if (!err) return resolve({ code: 0, stderr });
    const c = err.code;
    if (c === 'ENOENT' || c === 'EINVAL') return resolve({ code: c, stderr });
    resolve({ code: typeof c === 'number' ? c : 'ERROR', stderr: stderr || err.message });
  };
  try {
    execFile(file, args, { shell: opts.shell, windowsHide: true, timeout: 30_000 }, (err, _out, stderr) => finish(err, String(stderr)));
  } catch (err) {
    finish(err as Error, '');
  }
});

export type ClaudeCodeOutcome =
  | { status: 'registered' }
  | { status: 'unavailable'; reason: string }
  | { status: 'failed'; message: string };

/**
 * Registers via `claude mcp add-json --scope user`. Order: `claude` (exe, shell:false), then `claude.cmd`.
 * A .cmd shim cannot be started with shell:false (Node >= 18.20 rejects it with EINVAL, CVE-2024-27980),
 * so it is run through cmd.exe only with quoted arguments and only when the payload has no cmd metacharacters.
 * Anything else is reported as 'unavailable' so the caller can offer the command to copy.
 */
export async function registerClaudeCode(entry: McpEntry, run: Runner = defaultRunner): Promise<ClaudeCodeOutcome> {
  const add = addJsonArgs(entry);
  const remove = ['mcp', 'remove', '--scope', 'user', SERVER_KEY];
  const attempt = (file: string, shell: boolean, args: string[]) =>
    run(file, shell ? args.map(cmdQuote) : args, { shell });

  let file = 'claude';
  let shell = false;
  let probe = await attempt(file, shell, remove);
  if (probe.code === 'ENOENT' || probe.code === 'EINVAL') {
    if (CMD_UNSAFE.test(add[add.length - 1])) {
      return { status: 'unavailable', reason: 'claude.cmd cannot be started safely with these paths' };
    }
    file = 'claude.cmd'; shell = true;
    probe = await attempt(file, shell, remove);
    if (probe.code === 'ENOENT' || probe.code === 'EINVAL') {
      return { status: 'unavailable', reason: 'the claude CLI was not found' };
    }
  }
  // `remove` failing (server not registered yet) is expected and ignored.
  const res = await attempt(file, shell, add);
  return res.code === 0 ? { status: 'registered' } : { status: 'failed', message: res.stderr.trim() || `claude exited with ${String(res.code)}` };
}
