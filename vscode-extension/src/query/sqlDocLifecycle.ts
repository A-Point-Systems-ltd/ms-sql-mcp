import * as fs from 'fs';
import * as vscode from 'vscode';
import type { ConnectionProfile } from '../connections/profile';
import type { ConnectionStore } from '../connections/store';
import { removeLegacyEdits } from '../explorer/editableDdl';
import { Logger } from '../logger';
import { findProfile } from './editorState';
import { QueryDocuments, afterCloseGrace, keepOnActivation, orphanQueryIds } from './queryDocuments';
import {
  BackingRead, QueryIndex, QueryIndexEntry, closedEntry, closedQueryAction, isBlank, mergeRecovered, queryPrunePlan,
  recentQueries, recoveredEntries, relativeTime, reopenObjectName, withEntry, withOpenCleared, withoutIds, writtenEntry,
} from './queryIndex';
import type { QueryIndexFile } from './queryIndexFile';
import { SqlDocFileSystem, openDocumentKeys, openSqlDocs } from './sqlDocFs';
import { QueryCounter, SqlDocAddress, docTitle, newQueryId, profileTarget, queryNumberOf, queryObjectName } from './sqlDocNames';
import { TITLE_AFTER_SAVE_MESSAGE, titleAction } from './sqlDocTitles';

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
 * - closes (after the reopen grace: a blank query document is deleted, any other is kept and marked closed);
 * - renamed files;
 * - tab titles: an open document whose connection (Change Connection) or profile server/database changed is saved,
 *   closed and reopened under the new title with the same id and binding; a dirty one only after its next save;
 * - the deferred activation prune: the legacy `edits/` folder, stale associations, query documents closed more than
 *   30 days ago and blank orphans. Object backing files are never pruned (an unapplied edit must not be lost).
 * A backing file or index that cannot be read (other than "does not exist") is left alone.
 */
export class SqlDocLifecycle implements vscode.Disposable {
  private readonly subs: vscode.Disposable[] = [];
  private readonly timers = new Set<NodeJS.Timeout>();
  private readonly owned: OwnedQueryIds;
  private readonly index: QueryIndexFile;
  private readonly counter = new QueryCounter();
  /** Ids being reopened under a new title (and for the close grace after it): their old tab's close is not a close. */
  private readonly retitling = new Set<string>();
  /** Dirty documents (by uri) whose title is out of date: the title they get after the next save. */
  private readonly pendingTitles = new Map<string, string>();
  private readonly retitled = new vscode.EventEmitter<{ oldKey: string; newKey: string }>();
  /** Fires when a document was reopened under a new title, before its old tab closes (results can move over). */
  readonly onDidRetitle = this.retitled.event;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly docs: QueryDocuments,
    private readonly sqlDocs: SqlDocFileSystem,
    private readonly log: Logger,
    private readonly store: ConnectionStore,
    /** Whether a query runs in the document with this uri (its tab is never closed then). */
    private readonly isRunning: (key: string) => boolean,
  ) {
    this.owned = new OwnedQueryIds(context.workspaceState);
    this.index = sqlDocs.queryIndex();
    // A damaged index was moved aside (kept as .bad-<stamp>): rebuild it from the backing files right away.
    this.index.onCorrupt = () => void this.reconcileIndex()
      .catch(err => log.warn('sqldocs', 'Rebuilding the query index failed (' + (err instanceof Error ? err.name : 'error') + ').'));
    this.subs.push(
      vscode.workspace.onDidOpenTextDocument(doc => this.onOpen(doc)),
      vscode.workspace.onDidCloseTextDocument(doc => this.onClose(doc)),
      vscode.workspace.onDidRenameFiles(e => this.onRename(e)),
      sqlDocs.onDidWriteDoc(uri => this.recordWrite(uri)),
      store.onDidChange(() => this.refreshTitles()),
      docs.onDidChange(() => this.refreshTitles()),
      // After the save completes (the document is clean again): a deferred rename can run now.
      vscode.workspace.onDidSaveTextDocument(doc => {
        if (this.pendingTitles.has(doc.uri.toString())) this.refreshTitle(doc);
      }),
    );
    for (const doc of vscode.workspace.textDocuments) this.onOpen(doc);
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
    let index: QueryIndex;
    try {
      index = await this.reconcileIndex();
    } catch {
      void vscode.window.showWarningMessage('APoint-ms-sql: the list of recent query windows could not be read. Details are in the APoint-ms-sql Output.');
      return;
    }
    const open = openSqlDocs('query');
    const now = Date.now();
    const items: (vscode.QuickPickItem & { entry: QueryIndexEntry & { id: string } })[] = [];
    const missing: string[] = [];
    for (const entry of recentQueries(index)) {
      if (!open.has(entry.id)) {
        const read = await this.sqlDocs.readBacking('query', entry.id);
        if (read.kind === 'missing') missing.push(entry.id);
        // Blank, gone or unreadable (left alone) entries are not offered.
        if (read.kind !== 'text' || isBlank(read.text)) continue;
      }
      items.push({ label: entry.title, description: entry.connection, detail: relativeTime(entry.updatedAt, now), entry });
    }
    if (missing.length) void this.index.update(i => withoutIds(i, missing));
    if (!items.length) {
      void vscode.window.showInformationMessage('APoint-ms-sql: no recent query windows.');
      return;
    }
    const picked = await vscode.window.showQuickPick(items, { placeHolder: 'Open a recent query window', matchOnDescription: true });
    if (picked) await this.reopen(picked.entry, store);
  }

  dispose(): void {
    this.retitled.dispose();
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
    // Open again: closedAt is cleared, so the prune never touches it while it is open.
    await this.index.update(i => (i[id] ? withEntry(i, id, writtenEntry(i[id], title, connection || undefined, i[id].updatedAt)) : i));
    await this.show(uri);
  }

  private async show(uri: vscode.Uri): Promise<void> {
    const doc = await this.sqlDocs.sqlDocument(await vscode.workspace.openTextDocument(uri));
    await vscode.window.showTextDocument(doc, { preview: false });
  }

  /** A restored or opened query document is open again: its entry loses closedAt. Every mssql-sql document gets sql. */
  private onOpen(doc: vscode.TextDocument): void {
    void this.sqlDocs.sqlDocument(doc);
    const addr = SqlDocFileSystem.address(doc.uri);
    // A restored tab loads its document only now: its title may be out of date.
    if (addr) this.refreshTitle(doc);
    if (addr?.kind !== 'query') return;
    void this.index.update(i => {
      const e = i[addr.id];
      return e && e.closedAt !== undefined ? withEntry(i, addr.id, writtenEntry(e, e.title, undefined, e.updatedAt)) : i;
    });
  }

  private openQueryNumbers(): Set<number> {
    const numbers = new Set<number>();
    for (const uri of openSqlDocs('query').values()) {
      const n = queryNumberOf(SqlDocFileSystem.address(uri)?.title ?? '');
      if (n !== undefined) numbers.add(n);
    }
    return numbers;
  }

  /** Every write of a query document (save, New Query) refreshes its index entry and clears closedAt. */
  private recordWrite(uri: vscode.Uri): void {
    const addr = SqlDocFileSystem.address(uri);
    if (addr?.kind !== 'query') return;
    const connection = this.docs.get(uri)?.connection;
    void this.index.update(i => withEntry(i, addr.id, writtenEntry(i[addr.id], addr.title, connection, Date.now())));
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
      // Reopened under a new title (or still open in another tab): the id is not closed.
      if (query && (this.retitling.has(query.id) || openSqlDocs('query').has(query.id))) return;
      if (query) {
        void this.closeQuery(query.id, query.title, assoc?.connection)
          .catch(err => this.log.debug('query', `Recording a closed query window failed: ${String(err)}`));
      }
    });
  }

  /** A blank closed query document is deleted; any other is kept and marked closed; an unreadable one is left alone. */
  private async closeQuery(id: string, title: string, connection: string | undefined): Promise<void> {
    const action = closedQueryAction(await this.sqlDocs.readBacking('query', id));
    if (action === 'leave') return;
    if (action === 'keep') {
      await this.index.update(i => withEntry(i, id, closedEntry(title, connection, Date.now(), i[id])));
      return;
    }
    if (action === 'delete' && !(await this.sqlDocs.deleteBacking('query', id))) return;
    if (await this.index.update(i => withoutIds(i, [id]))) await this.owned.remove([id]);
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

  /**
   * Re-adds index entries for non-blank query backing files that have none (self-healing after a lost or damaged
   * index) and returns the index. Throws when the index cannot be read (nothing is written then).
   */
  private async reconcileIndex(): Promise<QueryIndex> {
    const index = await this.index.read();
    const files: { id: string; mtimeMs: number; read: BackingRead }[] = [];
    for (const f of await this.sqlDocs.listBacking('query')) {
      if (!index[f.id]) files.push({ ...f, read: await this.sqlDocs.readBacking('query', f.id) });
    }
    const openTitles = new Map<string, string>();
    for (const [id, uri] of openSqlDocs('query')) openTitles.set(id, SqlDocFileSystem.address(uri)?.title ?? '');
    const recovered = recoveredEntries(index, files, openTitles, Date.now());
    if (!Object.keys(recovered).length) return index;
    this.log.info('sqldocs', `Re-added ${Object.keys(recovered).length} query window(s) to the recent list.`);
    await this.index.update(i => mergeRecovered(i, recovered));
    return this.index.read();
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
    } catch (err) {
      this.log.error('query', 'Pruning stale query documents failed', err);
    }
  }

  private async pruneQueries(): Promise<void> {
    // An unreadable index throws here: nothing is deleted then.
    await this.reconcileIndex();
    // Ids this window has open in any tab (also background tabs without a loaded document) are open: clear closedAt
    // first, so a missed open event can never get them pruned.
    const tabs = new Set(openSqlDocs('query').keys());
    if (!(await this.index.update(i => withOpenCleared(i, tabs)))) return;
    const index = await this.index.read();
    const open = new Set([...tabs, ...this.owned.session]);
    const owned = this.owned.all();
    const blank = new Set<string>();
    const missing = new Set<string>();
    for (const id of orphanQueryIds(owned, open)) {
      const read = await this.sqlDocs.readBacking('query', id);
      if (read.kind === 'missing') missing.add(id);
      else if (read.kind === 'text' && isBlank(read.text)) blank.add(id);
      // 'unknown': in neither set, so the plan leaves it alone.
    }
    const plan = queryPrunePlan({ index, owned, open, blank, missing, now: Date.now() });
    const drop = [...plan.forget];
    // An id leaves the index and the owned list only once its file is really gone.
    for (const id of plan.remove) if (await this.sqlDocs.deleteBacking('query', id)) drop.push(id);
    if (!drop.length) return;
    if (await this.index.update(i => withoutIds(i, drop))) await this.owned.remove(drop);
  }

  /** Re-checks one document's tab title (for example when a run in it ended). */
  refreshTitleOf(doc: vscode.TextDocument): void {
    if (SqlDocFileSystem.address(doc.uri)) this.refreshTitle(doc);
  }

  /** {@link refreshTitle} for every loaded `mssql-sql:` document. */
  private refreshTitles(): void {
    for (const doc of vscode.workspace.textDocuments) {
      if (SqlDocFileSystem.address(doc.uri)) this.refreshTitle(doc);
    }
  }

  /** Brings one document's tab title in line with its binding and profile (see {@link titleAction}). */
  private refreshTitle(doc: vscode.TextDocument): void {
    if (doc.isClosed) return;
    const address = SqlDocFileSystem.address(doc.uri);
    if (!address || this.retitling.has(address.id)) return;
    const key = doc.uri.toString();
    const assoc = this.docs.get(doc.uri);
    const profile = assoc ? findProfile(this.store.list(), assoc.connection) : undefined;
    const action = titleAction({ address, assoc, profile, isDirty: doc.isDirty, running: this.isRunning(key) });
    if (action.kind === 'none') {
      this.pendingTitles.delete(key);
      return;
    }
    if (action.kind === 'defer') {
      // The status bar already shows the new target; say once per new title that the tab follows on save.
      if (this.pendingTitles.get(key) !== action.title) {
        this.pendingTitles.set(key, action.title);
        void vscode.window.showInformationMessage(TITLE_AFTER_SAVE_MESSAGE);
      }
      return;
    }
    this.pendingTitles.delete(key);
    void this.retitle(doc, address, action.title)
      .catch(err => this.log.warn('sqldocs', `Updating a tab title failed: ${err instanceof Error ? err.message : String(err)}`));
  }

  /**
   * Saves a clean document through the provider (same id, so the same backing file; the query index gets the new
   * title), opens the same id under `title` in the same editor group with the binding kept, then closes the old tab.
   * Its undo history does not carry over. A document that became dirty meanwhile is left as it is (renamed after its
   * next save). Never a programmatic text edit.
   */
  private async retitle(doc: vscode.TextDocument, address: SqlDocAddress, title: string): Promise<void> {
    const oldKey = doc.uri.toString();
    const assoc = this.docs.get(doc.uri);
    if (!assoc || doc.isDirty || doc.isClosed) return;
    this.retitling.add(address.id);
    try {
      const newUri = SqlDocFileSystem.uriWithTitle(address.kind, address.id, title);
      await this.docs.set(newUri, assoc);
      await this.sqlDocs.writeContent(newUri, doc.getText());
      if (doc.isDirty || doc.isClosed) {
        await this.docs.delete(newUri.toString());
        if (!doc.isClosed) this.pendingTitles.set(oldKey, title);
        return;
      }
      const tabs = vscode.window.tabGroups.all.flatMap(g => g.tabs)
        .filter(t => t.input instanceof vscode.TabInputText && t.input.uri.toString() === oldKey);
      const wasActive = vscode.window.activeTextEditor?.document.uri.toString() === oldKey;
      const reopened = await this.sqlDocs.sqlDocument(await vscode.workspace.openTextDocument(newUri));
      await vscode.window.showTextDocument(reopened, { viewColumn: tabs[0]?.group.viewColumn, preview: false, preserveFocus: !wasActive });
      this.retitled.fire({ oldKey, newKey: newUri.toString() });
      if (tabs.length) await vscode.window.tabGroups.close(tabs, true);
      await this.docs.delete(oldKey);
      this.log.debug('sqldocs', `A ${address.kind} document was reopened under its new title.`);
    } finally {
      // Kept past the close grace of the old tab, so its close is never taken for a closed query window.
      this.later(REOPEN_GRACE_MS * 3, () => this.retitling.delete(address.id));
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
