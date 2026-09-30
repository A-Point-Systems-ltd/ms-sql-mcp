import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { ConnectionStore } from '../connections/store';
import { EXE_NAME, resolveExe } from '../exe';
import { Logger } from '../logger';
import { registerClaudeCode, manualCommand } from './claudeCode';
import { claudeDesktopConfigPaths, cursorConfigPath } from './clientPaths';
import { atomicWriteFile, writeClientConfig } from './configWriter';
import { buildConnectionsFile, needsSecretDecision } from './connectionsFile';
import { McpEntry } from './jsonMerge';
import { SERVER_KEY } from './naming';
import { copyStableExe } from './stableExe';

const PASSWORDS_FLAG = 'msSqlMcp.connectionsFileHasPasswords';
const FILE_NAME = 'connections.json';
const USE_PLACEHOLDERS = 'Use ${env:} placeholders';
const WRITE_PASSWORDS = 'Write passwords to a per-user file';

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
  return { file, ...result };
}

function buildEntry(exe: string, file: string): McpEntry {
  const cfg = vscode.workspace.getConfiguration('msSqlMcp');
  return {
    command: exe,
    args: [],
    env: {
      MSSQL_CONNECTIONS_FILE: file,
      USE_INSIGHTS_LAYER: cfg.get<boolean>('insights', true) ? 'true' : 'false',
      MSSQL_ALLOW_ADHOC_CONNECTIONS: cfg.get<boolean>('allowAdhocConnections', false) ? 'true' : 'false',
    },
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
        if (!fs.existsSync(connectionsFilePath(context))) return;
        const withPasswords = context.globalState.get<boolean>(PASSWORDS_FLAG, false);
        writeConnectionsFile(context, store, withPasswords).catch(err => log.error('registerClients', 'connections.json refresh failed', err));
      }, 300);
    }),
    { dispose: () => { if (timer) clearTimeout(timer); } },
  );
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
    else { report.push(`Claude Code: not registered (${r.reason}).`); copyCmd = manualCommand(entry); }
  }

  log.info('registerClients', report.join(' | '));
  const buttons = copyCmd ? ['Copy Claude Code command'] : [];
  const pick = await vscode.window.showInformationMessage(`MSSQL-MCP: ${report.join('\n')}`, { modal: report.length > 2 }, ...buttons);
  if (pick && copyCmd) await vscode.env.clipboard.writeText(copyCmd);
}
