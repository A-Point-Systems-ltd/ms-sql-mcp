import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { ConnectionStore } from '../connections/store';
import { EXE_NAME, resolveExe } from '../exe';
import { Logger } from '../logger';
import { registerClaudeCode, powershellCommand } from './claudeCode';
import { claudeDesktopConfigPaths, cursorConfigPath } from './clientPaths';
import { atomicWriteFile, writeClientConfig } from './configWriter';
import { externalClientEnv } from '../connections/serverEnv';
import { buildConnectionsFile, needsSecretDecision, refreshConnectionsFileOnDisk } from './connectionsFile';
import { McpEntry } from './jsonMerge';
import { SERVER_KEY } from './naming';
import { copyStableExe } from './stableExe';

const PASSWORDS_FLAG = 'msSqlMcp.connectionsFileHasPasswords';
const FILE_NAME = 'connections.json';
const USE_PLACEHOLDERS = 'Use ${env:} placeholders';
const WRITE_PASSWORDS = 'Write passwords to a per-user file';
const RE_REGISTER = 'Re-register';

/** Copies the bundled (or configured) exe to globalStorage/bin/<version>/ so client configs survive extension upgrades. */
export async function ensureStableExe(context: vscode.ExtensionContext): Promise<string> {
  const exe = resolveExe(context.extensionUri);
  if (!exe.ok) throw new Error(exe.reason);
  const version = context.extension.packageJSON.version as string;
  return copyStableExe(exe.path, context.globalStorageUri.fsPath, version, EXE_NAME);
}

function connectionsFilePath(context: vscode.ExtensionContext): string {
  return path.join(context.globalStorageUri.fsPath, FILE_NAME);
}

/** Writes globalStorage/connections.json (open profiles, no default). Returns the path and the env vars placeholders need. */
export async function writeConnectionsFile(context: vscode.ExtensionContext, store: ConnectionStore, includePasswords: boolean) {
  const result = buildConnectionsFile(store.list(), await store.passwords(), includePasswords);
  const file = connectionsFilePath(context);
  atomicWriteFile(file, result.json);
  await context.globalState.update(PASSWORDS_FLAG, includePasswords);
  return { file, envVars: result.envVars, skipped: result.skipped };
}

function buildEntry(exe: string, file: string): McpEntry {
  const cfg = vscode.workspace.getConfiguration('msSqlMcp');
  return {
    command: exe,
    args: [],
    env: externalClientEnv(file, {
      insights: cfg.get<boolean>('insights', true),
      allowAdhocConnections: cfg.get<boolean>('allowAdhocConnections', false),
    }),
  };
}

export function registerClientCommand(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger): void {
  context.subscriptions.push(vscode.commands.registerCommand('msSqlMcp.registerClients', async () => {
    try { await run(context, store, log); }
    catch (err) {
      log.error('registerClients', 'Command failed', err);
      void vscode.window.showErrorMessage(`MSSQL-MCP: ${err instanceof Error ? err.message : String(err)}`);
    }
  }));

  // Keep an existing connections.json current; never create it implicitly.
  let timer: NodeJS.Timeout | undefined;
  context.subscriptions.push(
    store.onDidChange(() => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        refreshConnectionsFile(context, store, log).catch(err => log.error('registerClients', 'connections.json refresh failed', err));
      }, 300);
    }),
    { dispose: () => { if (timer) clearTimeout(timer); } },
  );
}

/**
 * Rewrites an existing connections.json after a profile change. Close / remove / read-only always take effect; a
 * profile that needs a new ${env:} variable is left out until the user re-registers, and with no connections left
 * the file is deleted (registered clients then cannot start until a connection is opened and re-registered).
 */
async function refreshConnectionsFile(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger): Promise<void> {
  const withPasswords = context.globalState.get<boolean>(PASSWORDS_FLAG, false);
  const r = refreshConnectionsFileOnDisk(connectionsFilePath(context), store.list(), await store.passwords(), withPasswords);
  if (r.action === 'none') return;
  const pending = r.pending.length
    ? ` Not added yet (define ${r.pending.map(x => x.envVar).join(', ')}, then re-register): ${r.pending.map(x => `'${x.name}'`).join(', ')}.`
    : '';
  if (r.action === 'delete') {
    log.warn('registerClients', `connections.json deleted: no connections left.${pending}`);
    const pick = await vscode.window.showWarningMessage(
      `MSSQL-MCP: no open connections are left, so the connections file for Cursor / Claude was deleted. Registered clients will not start the server until you open a connection and re-register.${pending}`,
      RE_REGISTER);
    if (pick === RE_REGISTER) await vscode.commands.executeCommand('msSqlMcp.registerClients');
    return;
  }
  if (r.pending.length) {
    log.warn('registerClients', `connections.json updated; pending: ${r.pending.map(x => x.name).join(', ')}.`);
    const pick = await vscode.window.showWarningMessage(`MSSQL-MCP: the connections file for Cursor / Claude was updated.${pending}`, RE_REGISTER);
    if (pick === RE_REGISTER) await vscode.commands.executeCommand('msSqlMcp.registerClients');
  }
}

async function run(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger): Promise<void> {
  if (process.platform !== 'win32') {
    void vscode.window.showInformationMessage('MSSQL-MCP: registration is only supported on Windows.');
    return;
  }
  const profiles = store.list();
  if (!profiles.some(p => p.open)) {
    void vscode.window.showWarningMessage('MSSQL-MCP: open at least one connection before registering.');
    return;
  }
  const picks = await vscode.window.showQuickPick(
    [{ label: 'Cursor' }, { label: 'Claude Desktop' }, { label: 'Claude Code' }],
    { canPickMany: true, title: 'Register MSSQL-MCP with...', placeHolder: 'Select one or more clients' });
  if (!picks?.length) return;
  const chosen = new Set(picks.map(p => p.label));

  let includePasswords = false;
  if (needsSecretDecision(profiles)) {
    const choice = await vscode.window.showWarningMessage(
      'Some connections use SQL authentication. Write their passwords in clear text to a per-user connections file, or reference environment variables (MSSQLMCP_PWD_<NAME>) that you define yourself?',
      { modal: true }, WRITE_PASSWORDS, USE_PLACEHOLDERS);
    if (!choice) return;
    includePasswords = choice === WRITE_PASSWORDS;
  }

  const exe = await ensureStableExe(context);
  const written = await writeConnectionsFile(context, store, includePasswords);
  const entry = buildEntry(exe, written.file);
  const report: string[] = [];

  for (const s of written.skipped) report.push(`Skipped '${s.name}': ${s.reason}.`);
  if (written.envVars.length) report.push(`Define these user environment variables before starting the client: ${written.envVars.join(', ')}.`);

  const tryWrite = (label: string, p: string) => {
    try {
      const r = writeClientConfig(p, SERVER_KEY, entry);
      report.push(`${label}: updated ${r.path}${r.backup ? ` (backup ${path.basename(r.backup)})` : ''}. Restart it to load the server.`);
    } catch (err) {
      log.error('registerClients', `${label} failed`, err);
      report.push(`${label}: FAILED - ${err instanceof Error ? err.message : String(err)}`);
    }
  };
  if (chosen.has('Cursor')) tryWrite('Cursor', cursorConfigPath());
  if (chosen.has('Claude Desktop')) for (const p of claudeDesktopConfigPaths()) tryWrite('Claude Desktop', p);

  let copyCmd: string | undefined;
  if (chosen.has('Claude Code')) {
    const r = await registerClaudeCode(entry);
    if (r.status === 'registered') report.push('Claude Code: registered (user scope).');
    else if (r.status === 'failed') report.push(`Claude Code: FAILED - ${r.message}`);
    else { report.push(`Claude Code: not registered (${r.reason}). Use the button to copy a command for Windows PowerShell.`); copyCmd = powershellCommand(entry); }
  }

  log.info('registerClients', report.join(' | '));
  const buttons = copyCmd ? ['Copy PowerShell command'] : [];
  const pick = await vscode.window.showInformationMessage(`MSSQL-MCP: ${report.join('\n')}`, { modal: report.length > 2 }, ...buttons);
  if (pick && copyCmd) await vscode.env.clipboard.writeText(copyCmd);
}
