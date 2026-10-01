import * as fs from 'fs';
import * as vscode from 'vscode';
import type { ConnectionProfile } from '../connections/profile';
import type { ConnectionStore } from '../connections/store';
import { removeLegacyEdits } from '../explorer/editableDdl';
import { Logger } from '../logger';
import { findProfile } from './editorState';
import { QueryDocuments, afterCloseGrace, keepOnActivation, orphanQueryIds } from './queryDocuments';
import {
  QueryIndexEntry, closedQueryAction, isBlank, objectPrunePlan, queryPrunePlan, recentQueries, relativeTime, reopenObjectName,
  withEntry, withoutIds,
} from './queryIndex';
import { QueryIndexFile, SqlDocFileSystem, openDocumentKeys, openSqlDocs } from './sqlDocFs';
import { QueryCounter, docTitle, needsSqlLanguage, newQueryId, profileTarget, queryNumberOf, queryObjectName } from './sqlDocNames';

/** A closed document reopens at once when only its language changed; wait this long before acting on the close. */
export const REOPEN_GRACE_MS = 200;

/** The activation prune runs once, this long after activation (restored tabs are known by then), never in activate. */
const PRUNE_DELAY_MS = 15_000;

/**
 * The ids of the `mssql-sql:/query/` documents this workspace created, so the prune of blank documents never deletes
 * another window's (the backing files live in global storage).
 */
const QUERY_DOC_IDS_KEY = 'msSqlMcp.queryDocIds';

/** This workspace's query document ids (workspace state). */
class OwnedQueryIds {
  /** Created or reopened in this session: never pruned. */
  readonly session = new Set<string>();

  constructor(private readonly memento: vscode.Memento) {}

  all(): string[] {
    const v = this.memento.get<unknown>(QUERY_DOC_IDS_KEY, []);
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [];
  }

  async add(id: string): Promise<void> {
    this.session.add(id);
    const ids = this.all();
    if (!ids.includes(id)) await this.memento.update(QUERY_DOC_IDS_KEY, [...ids, id]);
  }

  async remove(ids: readonly string[]): Promise<void> {
    if (!ids.length) return;
    const drop = new Set(ids);
    await this.memento.update(QUERY_DOC_IDS_KEY, this.all().filter(id => !drop.has(id)));
  }
}

/**
 * The lifecycle of `mssql-sql:` documents and of their associations:
 * - New Query and Open Recent Query (kept query documents, `sqldocs/query-index.json`);
 * - the sql language (the uris have no extension);
 * - closes (after the reopen grace: a blank query document is deleted, any other is kept for Open Recent Query);
 * - renamed files;
 * - the deferred activation prune: the legacy `edits/` folder, stale associations, query documents not written for
 *   30 days and blank orphans, and unused object backing files older than 30 days.
 */
export class SqlDocLifecycle implements vscode.Disposable {
  private readonly subs: vscode.Disposable[] = [];
  private readonly timers = new Set<NodeJS.Timeout>();
  private readonly owned: OwnedQueryIds;
  private readonly index: QueryIndexFile;
  private readonly counter = new QueryCounter();
  /** Pending setTextDocumentLanguage calls by uri, so the open listener and the open flows do not both change it. */
  private readonly languageChanges = new Map<string, Thenable<vscode.TextDocument>>();

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly docs: QueryDocuments,
    private readonly sqlDocs: SqlDocFileSystem,
    private readonly log: Logger,
  ) {
    this.owned = new OwnedQueryIds(context.workspaceState);
    this.index = new QueryIndexFile(context.globalStorageUri.fsPath, log);
    this.subs.push(
      vscode.workspace.onDidOpenTextDocument(doc => void this.sqlDocument(doc)),
      vscode.workspace.onDidCloseTextDocument(doc => this.onClose(doc)),
      vscode.workspace.onDidRenameFiles(e => this.onRename(e)),
      sqlDocs.onDidWriteDoc(uri => this.recordWrite(uri)),
    );
    for (const doc of vscode.workspace.textDocuments) void this.sqlDocument(doc);
    this.later(PRUNE_DELAY_MS, () => void this.prune());
  }

  /** Creates, binds and shows an empty `Query N - <server> - <database>` document for `profile`. */
  async createQuery(profile: ConnectionProfile): Promise<void> {
    const id = newQueryId();
    const uri = SqlDocFileSystem.uri('query', id, queryObjectName(this.counter.next(this.openQueryNumbers())), profileTarget(profile));
    await this.owned.add(id);
    await this.docs.set(uri, { connection: profile.name, kind: 'query' });
    await this.sqlDocs.writeContent(uri, '');
    await this.show(uri);
  }

  /** Quick pick of the kept query documents (newest first); the picked one reopens with its id, rebound and retitled. */
  async openRecentQuery(store: ConnectionStore): Promise<void> {
    const index = await this.index.read();
    const open = openSqlDocs('query');
    const now = Date.now();
    const items: (vscode.QuickPickItem & { entry: QueryIndexEntry & { id: string } })[] = [];
    const missing: string[] = [];
    for (const entry of recentQueries(index)) {
      if (!open.has(entry.id)) {
        const text = await this.sqlDocs.readBacking('query', entry.id);
        if (text === undefined) missing.push(entry.id);
        if (text === undefined || isBlank(text)) continue;
      }
      items.push({ label: entry.title, description: entry.connection, detail: relativeTime(entry.updatedAt, now), entry });
    }
    if (missing.length) void this.index.update(i => withoutIds(i, missing));
    if (!items.length) {
      void vscode.window.showInformationMessage('MSSQL-MCP: no recent query windows.');
      return;
    }
    const picked = await vscode.window.showQuickPick(items, { placeHolder: 'Open a recent query window', matchOnDescription: true });
    if (picked) await this.reopen(picked.entry, store);
  }

  dispose(): void {
    for (const s of this.subs) s.dispose();
    for (const t of this.timers) clearTimeout(t);
    this.timers.clear();
  }

  private async reopen(entry: QueryIndexEntry & { id: string }, store: ConnectionStore): Promise<void> {
    const { id } = entry;
    const profile = findProfile(store.list(), entry.connection);
    const connection = profile?.name ?? entry.connection;
    await this.owned.add(id);
    const already = openSqlDocs('query').get(id);
    if (already) {
      if (!this.docs.get(already) && connection) await this.docs.set(already, { connection, kind: 'query' });
      await this.show(already);
      return;
    }
    // Title rules as for New Query: its own number unless an open tab has it, the profile's current server/database.
    // A removed profile keeps the recorded title (the status bar then says the connection was removed).
    const taken = this.openQueryNumbers();
    const title = profile
      ? docTitle(reopenObjectName(entry.title, taken, () => this.counter.next(taken)), profileTarget(profile))
      : entry.title;
    const uri = SqlDocFileSystem.uriWithTitle('query', id, title);
    if (connection) await this.docs.set(uri, { connection, kind: 'query' });
    await this.index.update(i => (i[id] ? withEntry(i, id, { ...i[id], title, connection }) : i));
    await this.show(uri);
  }

  private async show(uri: vscode.Uri): Promise<void> {
    const doc = await this.sqlDocument(await vscode.workspace.openTextDocument(uri));
    await vscode.window.showTextDocument(doc, { preview: false });
  }

  /** `doc` with language sql when it is a `mssql-sql:` document (setTextDocumentLanguage closes and reopens it). */
  private sqlDocument(doc: vscode.TextDocument): Thenable<vscode.TextDocument> {
    if (!needsSqlLanguage(doc.uri.scheme, doc.languageId)) return Promise.resolve(doc);
    const key = doc.uri.toString();
    const pending = this.languageChanges.get(key);
    if (pending) return pending;
    const change = vscode.languages.setTextDocumentLanguage(doc, 'sql').then(
      d => { this.languageChanges.delete(key); return d; },
      err => {
        this.languageChanges.delete(key);
        this.log.debug('sqldocs', `Setting the sql language failed: ${err instanceof Error ? err.message : String(err)}`);
        return doc;
      });
    this.languageChanges.set(key, change);
    return change;
  }

  private openQueryNumbers(): Set<number> {
    const numbers = new Set<number>();
    for (const uri of openSqlDocs('query').values()) {
      const n = queryNumberOf(SqlDocFileSystem.address(uri)?.title ?? '');
      if (n !== undefined) numbers.add(n);
    }
    return numbers;
  }

  /** Every write of a query document (save, New Query) refreshes its index entry. */
  private recordWrite(uri: vscode.Uri): void {
    const addr = SqlDocFileSystem.address(uri);
    if (addr?.kind !== 'query') return;
    const connection = this.docs.get(uri)?.connection;
    void this.index.update(i => withEntry(i, addr.id, { title: addr.title, connection: connection ?? i[addr.id]?.connection ?? '', updatedAt: Date.now() }));
  }

  private onClose(doc: vscode.TextDocument): void {
    const addr = SqlDocFileSystem.address(doc.uri);
    const query = addr?.kind === 'query' ? addr : undefined;
    const assoc = this.docs.get(doc.uri);
    if (!assoc && !query) return;
    const key = doc.uri.toString();
    const { scheme } = doc.uri;
    const { isDirty } = doc;
    this.later(REOPEN_GRACE_MS, () => {
      // A language change closes and reopens the same uri: that is a reopen, and nothing is dropped or deleted.
      if (afterCloseGrace({ scheme, isDirty, reopened: openDocumentKeys().has(key) }) !== 'drop') return;
      void this.docs.delete(key);
      if (query) {
        void this.closeQuery(query.id, query.title, assoc?.connection)
          .catch(err => this.log.debug('query', `Recording a closed query window failed: ${String(err)}`));
      }
    });
  }

  /** A blank closed query document is deleted; any other is kept and recorded for Open Recent Query. */
  private async closeQuery(id: string, title: string, connection: string | undefined): Promise<void> {
    const action = closedQueryAction(await this.sqlDocs.readBacking('query', id));
    if (action === 'keep') {
      await this.index.update(i => withEntry(i, id, { title, connection: connection ?? i[id]?.connection ?? '', updatedAt: Date.now() }));
      return;
    }
    if (action === 'delete' && !(await this.sqlDocs.deleteBacking('query', id))) return;
    await this.index.update(i => withoutIds(i, [id]));
    await this.owned.remove([id]);
  }

  private onRename(e: vscode.FileRenameEvent): void {
    for (const { oldUri, newUri } of e.files) {
      const oldKey = oldUri.toString();
      const newKey = newUri.toString();
      for (const [key] of this.docs.all()) {
        // The file itself, or a file inside a renamed folder.
        if (key === oldKey) void this.docs.rename(key, newKey);
        else if (key.startsWith(`${oldKey}/`)) void this.docs.rename(key, newKey + key.slice(oldKey.length));
      }
    }
  }

  /** The deferred activation prune. Tabs and documents are read now, not at activation. */
  private async prune(): Promise<void> {
    try {
      // The legacy edits/ folder first, so its associations are gone before the prune looks for their files.
      await removeLegacyEdits(this.context.globalStorageUri.fsPath, this.docs, this.log);
      const open = openDocumentKeys();
      await this.docs.prune(key => {
        const uri = vscode.Uri.parse(key);
        return keepOnActivation(uri.scheme, open.has(key), () => fs.existsSync(uri.fsPath));
      });
      await this.pruneQueries();
      await this.pruneObjects();
    } catch (err) {
      this.log.error('query', 'Pruning stale query documents failed', err);
    }
  }

  private async pruneQueries(): Promise<void> {
    const open = new Set([...openSqlDocs('query').keys(), ...this.owned.session]);
    const owned = this.owned.all();
    const blank = new Set<string>();
    const missing = new Set<string>();
    for (const id of orphanQueryIds(owned, open)) {
      const text = await this.sqlDocs.readBacking('query', id);
      if (text === undefined) missing.add(id);
      else if (isBlank(text)) blank.add(id);
    }
    const plan = queryPrunePlan({ index: await this.index.read(), owned, open, blank, missing, now: Date.now() });
    const drop = [...plan.forget];
    // An id leaves the index and the owned list only once its file is really gone.
    for (const id of plan.remove) if (await this.sqlDocs.deleteBacking('query', id)) drop.push(id);
    if (!drop.length) return;
    await this.index.update(i => withoutIds(i, drop));
    await this.owned.remove(drop);
  }

  private async pruneObjects(): Promise<void> {
    const open = new Set(openSqlDocs('object').keys());
    const bound = new Set<string>();
    for (const [key] of this.docs.all()) {
      const addr = SqlDocFileSystem.address(vscode.Uri.parse(key));
      if (addr?.kind === 'object') bound.add(addr.id);
    }
    for (const id of objectPrunePlan(await this.sqlDocs.listBacking('object'), open, bound, Date.now())) {
      await this.sqlDocs.deleteBacking('object', id);
    }
  }

  private later(ms: number, fn: () => void): void {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      fn();
    }, ms);
    this.timers.add(timer);
  }
}
