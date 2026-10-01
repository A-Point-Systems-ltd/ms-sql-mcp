import * as vscode from 'vscode';
import { Logger } from '../logger';
import { ConnectionProfile } from './profile';
import { ConnectionStore } from './store';
import { PROBE_TIMEOUT_MS, probeConnection } from './probe';
import { ConnectionFormManager } from './connectionForm';
import { missingPasswordMessage, missingPasswords } from './serverEnv';
import { describeServerInfo, withConnectionHint } from './serverInfo';
import type { ExplorerClient } from '../explorer/explorerClient';

/** Name from a command argument: a string, or a tree element carrying `name` / `profile.name`. */
function argName(arg: unknown): string | undefined {
  if (typeof arg === 'string') return arg;
  const a = arg as { name?: unknown; profile?: { name?: unknown } } | undefined;
  if (typeof a?.name === 'string') return a.name;
  if (typeof a?.profile?.name === 'string') return a.profile.name;
  return undefined;
}

/**
 * The profile a command acts on: the one named by `arg` (an error when it does not exist), else a quick pick of the
 * profiles that pass `filter`.
 */
export async function pickProfile(store: ConnectionStore, arg: unknown, placeHolder: string, filter?: (p: ConnectionProfile) => boolean): Promise<ConnectionProfile | undefined> {
  const all = store.list();
  const wanted = argName(arg);
  if (wanted) {
    const found = all.find(p => p.name.toLowerCase() === wanted.toLowerCase());
    if (!found) void vscode.window.showErrorMessage(`MSSQL-MCP: connection '${wanted}' was not found.`);
    return found;
  }
  const items = all.filter(p => !filter || filter(p));
  if (!items.length) {
    void vscode.window.showInformationMessage('MSSQL-MCP: no matching connections.');
    return undefined;
  }
  const picked = await vscode.window.showQuickPick(
    items.map(p => ({ label: p.name, description: p.auth === 'raw' ? 'connection string' : `${p.server} / ${p.database}` })),
    { placeHolder });
  return picked ? all.find(p => p.name === picked.label) : undefined;
}

/** True for the editConnection argument of Show DDL History's "Set up…" button: `{ name, setUpHistory: true }`. */
function wantsHistorySetup(arg: unknown): boolean {
  return !!arg && typeof arg === 'object' && (arg as { setUpHistory?: unknown }).setUpHistory === true;
}

/**
 * Registers the connection commands. `setUpHistory` runs the DDL history set-up after a form save that turned
 * `ddlHistory` on (the runner process serves it).
 */
export function registerConnectionCommands(
  context: vscode.ExtensionContext, store: ConnectionStore, log: Logger, explorer: ExplorerClient,
  setUpHistory?: (profile: ConnectionProfile) => Promise<void>,
): void {
  const reg = (id: string, fn: (arg?: unknown) => Promise<void>) =>
    context.subscriptions.push(vscode.commands.registerCommand(`msSqlMcp.${id}`, async (arg?: unknown) => {
      try { await fn(arg); }
      catch (err) {
        log.error(id, 'Command failed', err);
        void vscode.window.showErrorMessage(`MSSQL-MCP: ${err instanceof Error ? err.message : String(err)}`);
      }
    }));

  const form = new ConnectionFormManager(context.extensionUri, store, log, setUpHistory);
  context.subscriptions.push(form);

  reg('addConnection', () => form.open());
  reg('editConnection', async arg => {
    const p = await pickProfile(store, arg, 'Connection to edit');
    if (p) await form.open(p, { setUpHistory: wantsHistorySetup(arg) });
  });
  reg('removeConnection', async arg => {
    const p = await pickProfile(store, arg, 'Connection to remove');
    if (!p) return;
    const ok = await vscode.window.showWarningMessage(`Remove connection '${p.name}'? Its saved password is deleted too.`, { modal: true }, 'Remove');
    if (ok === 'Remove') await store.remove(p.name);
  });
  reg('openConnection', async arg => {
    const p = await pickProfile(store, arg, 'Connection to open', x => !x.open);
    if (p) await store.setOpen(p.name, true);
  });
  reg('closeConnection', async arg => {
    const p = await pickProfile(store, arg, 'Connection to close', x => x.open);
    if (p) await store.setOpen(p.name, false);
  });
  reg('testConnection', async arg => {
    const p = await pickProfile(store, arg, 'Connection to test');
    if (!p) return;
    const passwords = await store.passwords();
    if (missingPasswords([{ ...p, open: true }], passwords).length) {
      void vscode.window.showWarningMessage(missingPasswordMessage(p.name));
      return;
    }
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Testing '${p.name}'...`, cancellable: true }, async (_progress, token) => {
      const ac = new AbortController();
      const timer = setTimeout(() => ac.abort(new Error(`Timed out after ${PROBE_TIMEOUT_MS / 1000} s.`)), PROBE_TIMEOUT_MS);
      let onAbort: (() => void) | undefined;
      const sub = token.onCancellationRequested(() => ac.abort(new Error('Cancelled.')));
      try {
        // Open profiles are served by the shared explorer process; closed ones need a one-off probe process.
        const summary = p.open
          ? await Promise.race([
              explorer.call(p.name, 'get_server_info', {}).then(describeServerInfo),
              new Promise<never>((_, reject) => {
                if (ac.signal.aborted) { reject(ac.signal.reason); return; }
                onAbort = () => reject(ac.signal.reason);
                ac.signal.addEventListener('abort', onAbort, { once: true });
              }),
            ])
          : await probeConnection(context.extensionUri, p, passwords, log, ac.signal);
        void vscode.window.showInformationMessage(`MSSQL-MCP '${p.name}': ${summary}`);
      } catch (err) {
        if (token.isCancellationRequested) return;
        log.error('testConnection', `Test of '${p.name}' failed`, err);
        void vscode.window.showErrorMessage(`MSSQL-MCP '${p.name}' failed: ${withConnectionHint(err instanceof Error ? err.message : String(err))}`);
      } finally {
        clearTimeout(timer);
        if (onAbort) ac.signal.removeEventListener('abort', onAbort);
        sub.dispose();
      }
    });
  });
}
