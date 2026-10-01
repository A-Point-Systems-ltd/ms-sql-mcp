import * as vscode from 'vscode';
import { formatError } from './errorFormat';
import { redact } from './redact';

export type LogLevel = 'off' | 'error' | 'warn' | 'info' | 'debug' | 'trace';

const ORDER: Record<LogLevel, number> = {
  off: 0,
  error: 1,
  warn: 2,
  info: 3,
  debug: 4,
  trace: 5,
};

const CONFIG_SECTION = 'msSqlMcp';
const MAX_PAYLOAD_CHARS = 2000;

/**
 * Operational log for the object tree / editor channel.
 *
 * Level resolution:
 *  - If the user explicitly set `msSqlMcp.logLevel`, that wins (runtime opt-in).
 *  - Otherwise: verbose (`debug`) when running in Development/Test (the testing
 *    environment), and `error` in a normally installed build — failures always land
 *    in the channel unless the user explicitly chose `off`.
 */
export class Logger {
  private level: LogLevel = 'off';

  constructor(
    private readonly channel: vscode.OutputChannel,
    private readonly mode: vscode.ExtensionMode,
  ) {
    this.refresh();
  }

  /** Re-read the configured level (call on configuration change). */
  refresh(): void {
    const inspected = vscode.workspace
      .getConfiguration(CONFIG_SECTION)
      .inspect<LogLevel>('logLevel');
    const explicit =
      inspected?.workspaceFolderValue ?? inspected?.workspaceValue ?? inspected?.globalValue;

    const previous = this.level;
    if (explicit && explicit in ORDER) {
      this.level = explicit;
    } else {
      this.level = this.mode === vscode.ExtensionMode.Production ? 'error' : 'debug';
    }
    if (previous !== this.level) {
      // Written unconditionally so the channel always explains its own verbosity.
      this.write('INFO', 'log', `Log level is '${this.level}' (extension mode: ${modeName(this.mode)}).`);
    }
  }

  get currentLevel(): LogLevel {
    return this.level;
  }

  show(): void {
    this.channel.show(true);
  }

  error(scope: string, message: string, err?: unknown): void {
    if (!this.enabled('error')) {
      return;
    }
    this.write('ERROR', scope, message);
    for (const line of formatError(err)) {
      this.channel.appendLine(`    ${line}`);
    }
  }

  warn(scope: string, message: string): void {
    this.log('warn', 'WARN', scope, message);
  }

  info(scope: string, message: string): void {
    this.log('info', 'INFO', scope, message);
  }

  debug(scope: string, message: string): void {
    this.log('debug', 'DEBUG', scope, message);
  }

  /** Verbose payload logging — full JSON bodies, truncated and redacted. */
  trace(scope: string, message: string, payload?: unknown): void {
    if (!this.enabled('trace')) {
      return;
    }
    this.write('TRACE', scope, message);
    if (payload !== undefined) {
      this.channel.appendLine(indent(stringifyPayload(payload)));
    }
  }

  private log(level: LogLevel, label: string, scope: string, message: string): void {
    if (!this.enabled(level)) {
      return;
    }
    this.write(label, scope, message);
  }

  private enabled(level: LogLevel): boolean {
    return this.level !== 'off' && ORDER[this.level] >= ORDER[level];
  }

  private write(label: string, scope: string, message: string): void {
    this.channel.appendLine(`[${new Date().toISOString()}] [${label}] [${scope}] ${message}`);
  }
}

function modeName(mode: vscode.ExtensionMode): string {
  switch (mode) {
    case vscode.ExtensionMode.Development:
      return 'Development';
    case vscode.ExtensionMode.Test:
      return 'Test';
    default:
      return 'Production';
  }
}

function indent(text: string): string {
  return text
    .split('\n')
    .map((line) => `    ${line}`)
    .join('\n');
}

function stringifyPayload(payload: unknown): string {
  let text: string;
  try {
    text = JSON.stringify(redact(payload), null, 2) ?? String(payload);
  } catch {
    text = String(payload);
  }
  return text.length > MAX_PAYLOAD_CHARS
    ? `${text.slice(0, MAX_PAYLOAD_CHARS)}\n… (${text.length - MAX_PAYLOAD_CHARS} more chars truncated)`
    : text;
}

