import * as vscode from 'vscode';
import { McpStdioClient } from '../client/mcpStdioClient';
import { pick } from '../client/parse';
import { runsHistorySetup } from '../history/historyModel';
import { Logger } from '../logger';
import { makeNonce } from '../webviewUtil';
import { renderConnectionForm } from './connectionFormHtml';
import {
  FormValues, ONLINE, PROBE_NAME, canBringOnline, databaseLabel, defaultFormValues, describeDatabaseList, formToProbeProfile, formToProfile,
  isDatabaseUnavailableError, isOnline, stateCacheKey, parseDatabaseList, parseFormMessage, profileToFormValues,
} from './connectionFormModel';
import { PROBE_TIMEOUT_MS, withProbeClient } from './probe';
import { describeServerInfo, withConnectionHint } from './serverInfo';
import { ConnectionProfile } from './profile';
import { ConnectionStore } from './store';

const ADD_KEY = '\0add';
/** Bringing a database online runs crash recovery; the server allows 300 s for the ALTER itself. */
const BRING_ONLINE_TIMEOUT_MS = 330_000;
const BRING_ONLINE_AND_SAVE = 'Bring online & save';
/** The optional OFFLINE check before saving a new connection must not hold Save for long (e.g. off VPN). */
const PRECHECK_TIMEOUT_MS = 10_000;

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
  /** Lower-cased database name -> state_desc learned by List databases / Test / Bring online (no re-probe on Save). */
  states: Map<string, string>;
  /** {@link stateCacheKey} of the values `states` was learned for. */
  statesKey: string;
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
    const form: OpenForm = { panel, working: false, closed: false, historyBaseline, states: new Map(), statesKey: '' };
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

  private async handle(
    form: OpenForm, type: 'test' | 'listDatabases' | 'save' | 'bringOnline', v: FormValues, existing: ConnectionProfile | undefined,
    hasSavedPassword: boolean,
  ): Promise<void> {
    const key = stateCacheKey(v);
    if (key !== form.statesKey) {
      form.states.clear();
      form.statesKey = key;
    }

    if (type === 'save') {
      // Adding a connection to an OFFLINE database: optionally bring it online first (the user decides in a modal).
      // Skipped for raw strings (the form has no database field) and Entra interactive (it would open a sign-in
      // window just to save); a state already learned in this form is reused instead of probing again.
      if (!existing && v.auth !== 'raw' && v.auth !== 'entraInteractive' && v.database.trim()) {
        const db = v.database.trim().toLowerCase();
        const state = form.states.has(db)
          ? form.states.get(db)
          : await this.probe(form, v, existing, 'state check', PRECHECK_TIMEOUT_MS, client => databaseState(client), { quiet: true });
        if (state && canBringOnline(state)) {
          const choice = await vscode.window.showWarningMessage(
            `Database '${v.database.trim()}' on ${v.server.trim()} is OFFLINE. Bring it online before saving?`,
            { modal: true, detail: BRING_ONLINE_DETAIL }, BRING_ONLINE_AND_SAVE, 'Save anyway');
          if (!choice) return;
          if (choice === BRING_ONLINE_AND_SAVE && !(await this.bringOnline(form, v, existing))) return;
        }
      }
      await this.save(form, v, existing, hasSavedPassword);
      return;
    }

    if (type === 'bringOnline') {
      const db = v.database.trim();
      if (!db) {
        await this.post(form, { type: 'errors', errors: { database: 'Enter the database to bring online.' } });
        return;
      }
      const choice = await vscode.window.showWarningMessage(
        `Bring database '${db}' on ${describeServer(v)} online?`, { modal: true, detail: BRING_ONLINE_DETAIL }, 'Bring online');
      if (choice) await this.bringOnline(form, v, existing);
      return;
    }

    if (type === 'test') {
      const outcome = await this.probe(form, v, existing, type, PROBE_TIMEOUT_MS, async client => {
        try {
          return { ok: true, text: describeServerInfo(await client.callTool('get_server_info', { connection: PROBE_NAME })) };
        } catch (err) {
          // A database that is not ONLINE fails every connection to it: say so, and let the form offer the fix. Only
          // for "cannot open database" errors: after a failed login (wrong password) a second login attempt would
          // count twice toward a lockout policy.
          const message = err instanceof Error ? err.message : String(err);
          if (!isDatabaseUnavailableError(message)) throw err;
          const state = await databaseState(client).catch(() => undefined);
          if (!state || isOnline(state)) throw err;
          return { ok: false, text: `Database '${v.database.trim()}' is ${state}. ${message}`, state };
        }
      });
      if (outcome) {
        await this.post(form, { type: 'testResult', ok: outcome.ok, text: outcome.text });
        if (outcome.state) await this.setState(form, v.database.trim(), outcome.state);
      }
      return;
    }

    // probe_list_databases goes through master; the override only lets an empty Database field pass validation.
    const databases = await this.probe(form, v, existing, type, PROBE_TIMEOUT_MS,
      async client => parseDatabaseList(pick(await client.callTool(PROBE_TOOLS.list, { connection: PROBE_NAME }), 'data')),
      { database: v.auth !== 'raw' && !v.database.trim() ? 'master' : undefined });
    if (databases) {
      form.states = new Map(databases.map(d => [d.name.toLowerCase(), d.state]));
      const items = databases.map(d => ({ ...d, label: databaseLabel(d) }));
      await this.post(form, { type: 'databases', databases: items, text: describeDatabaseList(databases) });
    }
  }

  /** Runs probe_bring_online after the user confirmed; true when the database is ONLINE afterwards. */
  private async bringOnline(form: OpenForm, v: FormValues, existing: ConnectionProfile | undefined): Promise<boolean> {
    const db = v.database.trim();
    const result = await this.probe(form, v, existing, 'bring online', BRING_ONLINE_TIMEOUT_MS,
      client => client.callTool(PROBE_TOOLS.bringOnline, { connection: PROBE_NAME, database: db }, { timeoutMs: BRING_ONLINE_TIMEOUT_MS }));
    if (result === undefined) {
      // Unknown now (e.g. it was not OFFLINE after all): ask again next time instead of trusting the old state.
      form.states.delete(db.toLowerCase());
      return false;
    }
    this.log.info('connectionForm', `Database '${db}' brought online from the connection form.`);
    await this.post(form, { type: 'testResult', ok: true, text: `Database '${db}' is now ONLINE.` });
    await this.setState(form, db, ONLINE);
    return true;
  }

  /** Remembers a database's state for this form and shows it in the page. */
  private async setState(form: OpenForm, database: string, state: string): Promise<void> {
    form.states.set(database.toLowerCase(), state);
    await this.post(form, { type: 'databaseState', database, state });
  }

  /**
   * Runs `fn` on a short-lived probe process for the form's unsaved values (an empty password falls back to the saved
   * one when editing). Shows busy, validation errors and failures in the form (`quiet` hides them); returns undefined
   * on any failure. The probe_* tools reach the server through master, so the form's own database may be offline.
   */
  private async probe<T>(
    form: OpenForm, v: FormValues, existing: ConnectionProfile | undefined, what: string, timeoutMs: number,
    fn: (client: McpStdioClient) => Promise<T>, opts: { quiet?: boolean; database?: string } = {},
  ): Promise<T | undefined> {
    const savedPassword = existing?.auth === 'sql' ? (await this.store.passwords()).get(existing.name) : undefined;
    const password = v.auth === 'sql' ? (v.password || savedPassword) : undefined;
    const { profile, errors } = formToProbeProfile(v, { hasPassword: !!password, database: opts.database });
    if (!profile) {
      if (!opts.quiet) await this.post(form, { type: 'errors', errors });
      return undefined;
    }
    const passwords = new Map<string, string>(password ? [[PROBE_NAME, password]] : []);

    const ac = new AbortController();
    form.abort = ac;
    const timer = setTimeout(() => ac.abort(new Error(`Timed out after ${timeoutMs / 1000} s.`)), timeoutMs);
    await this.post(form, { type: 'busy', busy: true });
    try {
      return await withProbeClient(this.extensionUri, profile, passwords, this.log, ac.signal, fn);
    } catch (err) {
      if (form.closed) return undefined;
      this.log.error('connectionForm', `${what} failed`, err);
      if (!opts.quiet) {
        await this.post(form, { type: 'testResult', ok: false, text: withConnectionHint(err instanceof Error ? err.message : String(err)) });
      }
      return undefined;
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

/** The probe process's tools (MSSQL_PROBE_TOOLS); never listed to agents. */
const PROBE_TOOLS = {
  list: 'probe_list_databases',
  state: 'probe_database_state',
  bringOnline: 'probe_bring_online',
} as const;

const BRING_ONLINE_DETAIL =
  'Runs ALTER DATABASE ... SET ONLINE on the server. If the database was taken offline on purpose (maintenance, ' +
  'restore), applications can start using it again. Needs ALTER permission on the database (for example dbcreator).';

/** The form's database state (probe_database_state), or undefined when no such database exists. */
async function databaseState(client: McpStdioClient): Promise<string | undefined> {
  const state = pick(pick(await client.callTool(PROBE_TOOLS.state, { connection: PROBE_NAME }), 'data'), 'state');
  return typeof state === 'string' ? state : undefined;
}

function describeServer(v: FormValues): string {
  return v.auth === 'raw' ? "the connection string's server" : v.server.trim();
}
