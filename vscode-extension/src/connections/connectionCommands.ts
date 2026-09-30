import * as vscode from 'vscode';
import { Logger } from '../logger';
import { AuthKind, ConnectionProfile, validateProfile } from './profile';
import { ConnectionStore } from './store';
import { probeConnection } from './probe';
import { missingPasswordMessage, missingPasswords } from './serverEnv';
import { describeServerInfo, withConnectionHint } from './serverInfo';
import type { ExplorerClient } from '../explorer/explorerClient';

const TEST_TIMEOUT_MS = 30_000;

const ENCRYPTION_OPTIONS: Array<{ label: string; detail: string; encrypt: ConnectionProfile['encrypt']; trust: boolean }> = [
  { label: 'Mandatory (verify certificate)', detail: 'Encrypted; the server certificate must be trusted by this machine.', encrypt: 'mandatory', trust: false },
  { label: 'Mandatory, trust server certificate (self-signed / on-prem)', detail: 'Encrypted, but the certificate is not verified: safe against sniffing, not against a man-in-the-middle. Use on trusted networks.', encrypt: 'mandatory', trust: true },
  { label: 'Optional (no encryption)', detail: 'Traffic may be unencrypted, including credentials on SQL login. Legacy servers only.', encrypt: 'optional', trust: true },
  { label: 'Strict (TDS 8)', detail: 'TLS 1.3 from the first byte; requires SQL Server 2022+ with a trusted certificate.', encrypt: 'strict', trust: false },
];

const AUTH_LABELS: Array<{ label: string; kind: AuthKind }> = [
  { label: 'Windows integrated', kind: 'windows' },
  { label: 'SQL login (user + password)', kind: 'sql' },
  { label: 'Microsoft Entra - interactive', kind: 'entraInteractive' },
  { label: 'Microsoft Entra - default credential', kind: 'entraDefault' },
  { label: 'Raw connection string', kind: 'raw' },
];

/** Name from a command argument: a string, or a tree element carrying `name` / `profile.name`. */
function argName(arg: unknown): string | undefined {
  if (typeof arg === 'string') return arg;
  const a = arg as { name?: unknown; profile?: { name?: unknown } } | undefined;
  if (typeof a?.name === 'string') return a.name;
  if (typeof a?.profile?.name === 'string') return a.profile.name;
  return undefined;
}

async function pickProfile(store: ConnectionStore, arg: unknown, placeHolder: string, filter?: (p: ConnectionProfile) => boolean): Promise<ConnectionProfile | undefined> {
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

async function yesNo(title: string, yesDefault: boolean, detail: string): Promise<boolean | undefined> {
  const yes = { label: 'Yes', detail };
  const no = { label: 'No', detail };
  const picked = await vscode.window.showQuickPick(yesDefault ? [yes, no] : [no, yes], { title, placeHolder: title });
  return picked ? picked === yes : undefined;
}

/** Wizard for add (existing undefined) and edit. Returns undefined when cancelled. */
async function runWizard(store: ConnectionStore, existing?: ConnectionProfile): Promise<{ profile: ConnectionProfile; password?: string } | undefined> {
  const title = existing ? `Edit connection '${existing.name}'` : 'Add connection';

  let name = existing?.name;
  if (!name) {
    name = await vscode.window.showInputBox({
      title, prompt: 'Connection name (letters, digits, - _ .)', ignoreFocusOut: true,
      validateInput: v => {
        const errs = validateProfile({ name: v, server: 'x', database: 'x', auth: 'windows', readOnly: true, insights: true, open: true, encrypt: 'mandatory', trustServerCertificate: false });
        if (errs.length) return errs[0];
        return store.list().some(p => p.name.toLowerCase() === v.toLowerCase()) ? 'A connection with this name already exists.' : undefined;
      },
    });
    if (!name) return undefined;
  }

  const authPick = await vscode.window.showQuickPick(
    AUTH_LABELS.map(a => ({ label: a.label, authKind: a.kind, picked: a.kind === (existing?.auth ?? 'windows') })),
    { title, placeHolder: 'Authentication' });
  if (!authPick) return undefined;
  const auth = authPick.authKind;

  let server = existing?.server ?? '';
  let database = existing?.database ?? '';
  let user = existing?.user;
  let rawConnectionString: string | undefined;

  if (auth === 'raw') {
    rawConnectionString = await vscode.window.showInputBox({
      title, prompt: 'SQL Server connection string (must not contain a password; it is stored unencrypted)', value: existing?.rawConnectionString, ignoreFocusOut: true,
      validateInput: v => !v.trim() ? 'Connection string is required.'
        : /\b(password|pwd)\s*=/i.test(v) ? 'Remove the password - use another authentication type so it can be kept in secret storage.'
        : undefined,
    });
    if (rawConnectionString === undefined) return undefined;
    server = ''; database = ''; user = undefined;
  } else {
    const s = await vscode.window.showInputBox({ title, prompt: 'Server (host, host\\instance or host,port)', value: server, ignoreFocusOut: true, validateInput: v => v.trim() ? undefined : 'Server is required.' });
    if (s === undefined) return undefined;
    server = s.trim();
    const d = await vscode.window.showInputBox({ title, prompt: 'Database', value: database, ignoreFocusOut: true, validateInput: v => v.trim() ? undefined : 'Database is required.' });
    if (d === undefined) return undefined;
    database = d.trim();
    if (auth === 'sql' || auth === 'entraInteractive') {
      const u = await vscode.window.showInputBox({ title, prompt: 'User', value: user, ignoreFocusOut: true, validateInput: v => v.trim() ? undefined : 'User is required.' });
      if (u === undefined) return undefined;
      user = u.trim();
    } else {
      user = undefined;
    }
  }

  let encrypt: ConnectionProfile['encrypt'] = existing?.encrypt ?? 'mandatory';
  let trustServerCertificate = existing?.trustServerCertificate ?? true;
  if (auth !== 'raw') {
    const match = ENCRYPTION_OPTIONS.find(o => o.encrypt === encrypt && o.trust === trustServerCertificate);
    // An edited profile with a combo outside the four options keeps it unless the user picks another.
    const current = match ?? (existing
      ? { label: `Keep current (${encrypt}, trust=${trustServerCertificate})`, detail: 'Leave the encryption settings unchanged.', encrypt, trust: trustServerCertificate }
      : ENCRYPTION_OPTIONS[1]);
    const enc = await vscode.window.showQuickPick(
      [current, ...ENCRYPTION_OPTIONS.filter(o => o !== current)].map(o => ({ label: o.label, detail: o.detail, opt: o })),
      { title, placeHolder: 'Encryption' });
    if (!enc) return undefined;
    encrypt = enc.opt.encrypt;
    trustServerCertificate = enc.opt.trust;
  }

  let password: string | undefined;
  if (auth === 'sql') {
    const hasSaved = !!existing && (await store.passwords()).has(existing.name);
    const pw = await vscode.window.showInputBox({
      title, password: true, ignoreFocusOut: true,
      prompt: hasSaved ? 'Password (leave empty to keep the saved one)' : 'Password',
      validateInput: v => (v || hasSaved) ? undefined : 'Password is required.',
    });
    if (pw === undefined) return undefined;
    if (pw !== '') password = pw;
  }

  const readOnly = await yesNo('Read-only?', existing?.readOnly ?? true, 'Read-only connections refuse write tools and use ApplicationIntent=ReadOnly.');
  if (readOnly === undefined) return undefined;
  const insights = await yesNo('Enable AI Insights layer for this connection?', existing?.insights ?? true, 'Adds schema-insight tools for agents (also requires the msSqlMcp.insights setting).');
  if (insights === undefined) return undefined;

  const profile: ConnectionProfile = {
    name, server, database, auth, user, readOnly, insights,
    open: existing?.open ?? true,
    encrypt,
    trustServerCertificate,
    rawConnectionString,
  };
  return { profile, password };
}

export function registerConnectionCommands(context: vscode.ExtensionContext, store: ConnectionStore, log: Logger, explorer: ExplorerClient): void {
  const reg = (id: string, fn: (arg?: unknown) => Promise<void>) =>
    context.subscriptions.push(vscode.commands.registerCommand(`msSqlMcp.${id}`, async (arg?: unknown) => {
      try { await fn(arg); }
      catch (err) {
        log.error(id, 'Command failed', err);
        void vscode.window.showErrorMessage(`MSSQL-MCP: ${err instanceof Error ? err.message : String(err)}`);
      }
    }));

  const save = async (existing?: ConnectionProfile) => {
    const result = await runWizard(store, existing);
    if (!result) return;
    await store.upsert(result.profile, result.password);
    void vscode.window.showInformationMessage(`MSSQL-MCP: connection '${result.profile.name}' saved.`);
  };

  reg('addConnection', () => save());
  reg('editConnection', async arg => {
    const p = await pickProfile(store, arg, 'Connection to edit');
    if (p) await save(p);
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
      const timer = setTimeout(() => ac.abort(new Error(`Timed out after ${TEST_TIMEOUT_MS / 1000} s.`)), TEST_TIMEOUT_MS);
      const sub = token.onCancellationRequested(() => ac.abort(new Error('Cancelled.')));
      try {
        // Open profiles are served by the shared explorer process; closed ones need a one-off probe process.
        const summary = p.open
          ? await Promise.race([
              explorer.call(p.name, 'get_server_info', {}).then(describeServerInfo),
              new Promise<never>((_, reject) => {
                if (ac.signal.aborted) reject(ac.signal.reason);
                ac.signal.addEventListener('abort', () => reject(ac.signal.reason), { once: true });
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
        sub.dispose();
      }
    });
  });
}
