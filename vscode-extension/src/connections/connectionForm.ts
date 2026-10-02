import * as vscode from 'vscode';
import { pick } from '../client/parse';
import { parseReadData } from '../dataTable';
import { runsHistorySetup } from '../history/historyModel';
import { Logger } from '../logger';
import { makeNonce } from '../webviewUtil';
import { renderConnectionForm } from './connectionFormHtml';
import { FormValues, PROBE_NAME, defaultFormValues, formToProbeProfile, formToProfile, parseFormMessage, profileToFormValues } from './connectionFormModel';
import { PROBE_TIMEOUT_MS, withProbeClient } from './probe';
import { describeServerInfo, withConnectionHint } from './serverInfo';
import { ConnectionProfile } from './profile';
import { ConnectionStore } from './store';

const ADD_KEY = '\0add';
const LIST_DATABASES_SQL = 'SELECT name FROM sys.databases WHERE state = 0 ORDER BY name';

interface OpenForm {
  panel: vscode.WebviewPanel;
  /** Aborts a running Test / List databases (kills its server process). */
  abort?: AbortController;
  /** True while a message is being handled; further clicks are ignored (the buttons are disabled meanwhile). */
  working: boolean;
  closed: boolean;
  /**
   * `ddlHistory` before this form's edits (undefined when adding). A save that turns it on relative to this runs the
   * DDL history set-up; a form opened by "Set up…" uses false, so its save runs it whenever the box is checked.
   */
  historyBaseline: boolean | undefined;
}

export interface FormOpenOptions {
  /** Opened from Show DDL History's "Set up…": Save runs the DDL history set-up while the box is checked. */
  setUpHistory?: boolean;
}

/**
 * The add / edit connection form: one webview panel per connection name (and one for "add").
 * The password travels webview -> extension only and is never echoed back.
 */
export class ConnectionFormManager implements vscode.Disposable {
  private readonly forms = new Map<string, OpenForm>();

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly store: ConnectionStore,
    private readonly log: Logger,
    /**
     * Runs after a successful save that turned `ddlHistory` on, or that moved a connection with it on to another
     * server or database (see {@link runsHistorySetup}); never awaited by the save.
     */
    private readonly setUpHistory?: (profile: ConnectionProfile) => Promise<void>,
  ) {}

  /** Opens the form (existing undefined = add), or reveals the panel that is already open for it. */
  async open(existing?: ConnectionProfile, opts: FormOpenOptions = {}): Promise<void> {
    const key = existing ? existing.name.toLowerCase() : ADD_KEY;
    const open = this.forms.get(key);
    if (open) {
      if (opts.setUpHistory) open.historyBaseline = false;
      open.panel.reveal();
      return;
    }

    const hasSavedPassword = !!existing && existing.auth === 'sql' && (await this.store.passwords()).has(existing.name);
    const panel = vscode.window.createWebviewPanel(
      'msSqlMcp.connectionForm',
      existing ? `Edit connection '${existing.name}'` : 'Add connection',
      vscode.ViewColumn.Active,
      { enableScripts: true, retainContextWhenHidden: true },
    );
    const historyBaseline = !existing ? undefined : opts.setUpHistory ? false : existing.ddlHistory === true;
    const form: OpenForm = { panel, working: false, closed: false, historyBaseline };
    this.forms.set(key, form);
    panel.onDidDispose(() => {
      form.closed = true;
      form.abort?.abort(new Error('Form closed.'));
      this.forms.delete(key);
    });
    panel.webview.onDidReceiveMessage(raw => {
      const msg = parseFormMessage(raw);
      if (!msg) {
        this.log.warn('connectionForm', 'Ignored a malformed message from the form.');
        return;
      }
      if (msg.type === 'cancel') { panel.dispose(); return; }
      if (form.working) return;
      form.working = true;
      void this.handle(form, msg.type, msg.values, existing, hasSavedPassword)
        .catch(err => {
          this.log.error('connectionForm', `${msg.type} failed`, err);
          return this.post(form, { type: 'testResult', ok: false, text: err instanceof Error ? err.message : String(err) });
        })
        .finally(() => { form.working = false; });
    });

    const values = existing ? profileToFormValues(existing) : defaultFormValues();
    panel.webview.html = renderConnectionForm(values, {
      mode: existing ? 'edit' : 'add', hasSavedPassword, nonce: makeNonce(), cspSource: panel.webview.cspSource,
    });
  }

  dispose(): void {
    for (const f of [...this.forms.values()]) f.panel.dispose();
    this.forms.clear();
  }

  /** Posts to the webview; false when the panel is already gone. */
  private async post(form: OpenForm, message: unknown): Promise<boolean> {
    if (form.closed) return false;
    try { return await form.panel.webview.postMessage(message); }
    catch { return false; }
  }

  private async handle(form: OpenForm, type: 'test' | 'listDatabases' | 'save', v: FormValues, existing: ConnectionProfile | undefined, hasSavedPassword: boolean): Promise<void> {
    if (type === 'save') {
      await this.save(form, v, existing, hasSavedPassword);
      return;
    }
    // Probe with the unsaved values; an empty password field falls back to the saved one (edit).
    const savedPassword = existing?.auth === 'sql' ? (await this.store.passwords()).get(existing.name) : undefined;
    const password = v.auth === 'sql' ? (v.password || savedPassword) : undefined;
    const { profile, errors } = formToProbeProfile(v, { hasPassword: !!password, database: type === 'listDatabases' && v.auth !== 'raw' ? 'master' : undefined });
    if (!profile) {
      await this.post(form, { type: 'errors', errors });
      return;
    }
    const passwords = new Map<string, string>(password ? [[PROBE_NAME, password]] : []);

    const ac = new AbortController();
    form.abort = ac;
    const timer = setTimeout(() => ac.abort(new Error(`Timed out after ${PROBE_TIMEOUT_MS / 1000} s.`)), PROBE_TIMEOUT_MS);
    await this.post(form, { type: 'busy', busy: true });
    try {
      if (type === 'test') {
        const text = await withProbeClient(this.extensionUri, profile, passwords, this.log, ac.signal,
          async client => describeServerInfo(await client.callTool('get_server_info', { connection: PROBE_NAME })));
        await this.post(form, { type: 'testResult', ok: true, text });
      } else {
        const names = await withProbeClient(this.extensionUri, profile, passwords, this.log, ac.signal, async client => {
          const result = parseReadData(await client.callTool('read_data', { connection: PROBE_NAME, sql: LIST_DATABASES_SQL, maxRows: 2000 }));
          return result.rows.map(r => pick(r, 'name')).filter((n): n is string => typeof n === 'string');
        });
        await this.post(form, { type: 'databases', names });
      }
    } catch (err) {
      if (form.closed) return;
      this.log.error('connectionForm', `${type} failed`, err);
      await this.post(form, { type: 'testResult', ok: false, text: withConnectionHint(err instanceof Error ? err.message : String(err)) });
    } finally {
      clearTimeout(timer);
      form.abort = undefined;
      await this.post(form, { type: 'busy', busy: false });
    }
  }

  private async save(form: OpenForm, v: FormValues, existing: ConnectionProfile | undefined, hasSavedPassword: boolean): Promise<void> {
    const current = this.store.list();
    if (existing && !current.some(p => p.name === existing.name)) {
      await this.post(form, { type: 'testResult', ok: false, text: `Connection '${existing.name}' no longer exists. Close this form and add it again.` });
      return;
    }
    const { profile, password, errors } = formToProfile(v, { existing, hasSavedPassword, existingNames: current.map(p => p.name) });
    if (!profile) {
      await this.post(form, { type: 'errors', errors });
      return;
    }
    await this.store.upsert(profile, password);
    void vscode.window.showInformationMessage(`APoint-ms-sql: connection '${profile.name}' saved.`);
    form.panel.dispose();
    // After the store change: the runner's debounced reset is pending, and its next call applies it first, so the
    // status check runs against the saved profile set. An edit that points the connection at another server or
    // database runs it again for the new database.
    if (this.setUpHistory && runsHistorySetup(form.historyBaseline, profile, existing)) void this.setUpHistory(profile);
  }
}
