import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { Logger } from '../logger';
import { QUERY_INDEX_FILE, QueryIndex, parseQueryIndex, serializeQueryIndex } from './queryIndex';
import {
  SQL_DOCS_DIR, SQL_DOC_SCHEME, SqlDocAddress, SqlDocKind, TitleTarget, backingFile, docTitle, isSqlDocDirectory,
  isValidDocId, parseSqlDocPath, sqlDocPath, writeFileCheck,
} from './sqlDocNames';

const isNotFound = (err: unknown): boolean => (err as NodeJS.ErrnoException | undefined)?.code === 'ENOENT';
const message = (err: unknown): string => (err instanceof Error ? err.message : String(err));

/** The uris of every loaded document and every text tab (restored background tabs may have no document yet). */
export function openDocumentUris(): vscode.Uri[] {
  const byKey = new Map<string, vscode.Uri>();
  for (const d of vscode.workspace.textDocuments) byKey.set(d.uri.toString(), d.uri);
  for (const group of vscode.window.tabGroups.all) {
    for (const tab of group.tabs) {
      if (tab.input instanceof vscode.TabInputText) byKey.set(tab.input.uri.toString(), tab.input.uri);
    }
  }
  return [...byKey.values()];
}

/** `uri.toString()` of {@link openDocumentUris}. */
export function openDocumentKeys(): Set<string> {
  return new Set(openDocumentUris().map(u => u.toString()));
}

/** The open (loaded or tabbed) `mssql-sql:` documents of `kind`: id → uri (the first one seen). */
export function openSqlDocs(kind: SqlDocKind): Map<string, vscode.Uri> {
  const result = new Map<string, vscode.Uri>();
  for (const uri of openDocumentUris()) {
    const addr = SqlDocFileSystem.address(uri);
    if (addr?.kind === kind && !result.has(addr.id)) result.set(addr.id, uri);
  }
  return result;
}

/**
 * The `mssql-sql:` file system: `mssql-sql:/<kind>/<id>/<title>` is backed by `<globalStorage>/sqldocs/<kind>/<id>.sql`.
 * Only kind + id find the file; the title is the tab label. The extension changes a document's content only by writing
 * here and firing `onDidChangeFile` (the editor reloads a clean document), never with a programmatic text edit, so
 * Cursor shows no Keep / Undo review for it. Rename and directory operations are refused.
 */
export class SqlDocFileSystem implements vscode.FileSystemProvider, vscode.Disposable {
  private readonly emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile = this.emitter.event;
  private readonly writes = new vscode.EventEmitter<vscode.Uri>();
  /** Fires after every successful write of a document (editor save or {@link writeContent}). */
  readonly onDidWriteDoc = this.writes.event;

  /** @param storageRoot the extension's global storage folder (`context.globalStorageUri.fsPath`). */
  constructor(private readonly storageRoot: string, private readonly log: Logger) {}

  /** The document uri of `kind`/`id` with the tab title `<objectName> - <server> - <database>`. */
  static uri(kind: SqlDocKind, id: string, objectName: string, target: TitleTarget): vscode.Uri {
    return SqlDocFileSystem.uriWithTitle(kind, id, docTitle(objectName, target));
  }

  /** The document uri of `kind`/`id` with an already built title. */
  static uriWithTitle(kind: SqlDocKind, id: string, title: string): vscode.Uri {
    return vscode.Uri.from({ scheme: SQL_DOC_SCHEME, path: sqlDocPath({ kind, id, title }) });
  }

  /** The address of a `mssql-sql:` uri, or undefined for another scheme or a malformed path. */
  static address(uri: vscode.Uri): SqlDocAddress | undefined {
    return uri.scheme === SQL_DOC_SCHEME ? parseSqlDocPath(uri.path) : undefined;
  }

  /** Writes `content` as the document's text and tells the editor it changed (a clean open document reloads). */
  async writeContent(uri: vscode.Uri, content: string): Promise<void> {
    await this.write(uri, Buffer.from(content, 'utf8'), { create: true, overwrite: true });
  }

  /** The text of `kind`/`id`'s backing file, or undefined when it does not exist. */
  async readBacking(kind: SqlDocKind, id: string): Promise<string | undefined> {
    try {
      return await fs.readFile(backingFile(this.storageRoot, kind, id), 'utf8');
    } catch (err) {
      if (!isNotFound(err)) this.log.debug('sqldocs', `Reading a ${kind} document failed: ${message(err)}`);
      return undefined;
    }
  }

  /** Deletes the backing file of `kind`/`id`. True when it is gone afterwards (also when it was already gone). */
  async deleteBacking(kind: SqlDocKind, id: string): Promise<boolean> {
    try {
      await fs.rm(backingFile(this.storageRoot, kind, id), { force: true });
      return true;
    } catch (err) {
      this.log.debug('sqldocs', `Deleting a ${kind} document's backing file failed: ${message(err)}`);
      return false;
    }
  }

  /** The backing files of `kind` with their last write time (files with a malformed name are left out). */
  async listBacking(kind: SqlDocKind): Promise<{ id: string; mtimeMs: number }[]> {
    const dir = path.join(this.storageRoot, SQL_DOCS_DIR, kind);
    let names: string[];
    try {
      names = await fs.readdir(dir);
    } catch (err) {
      if (!isNotFound(err)) this.log.debug('sqldocs', `Listing ${kind} documents failed: ${message(err)}`);
      return [];
    }
    const result: { id: string; mtimeMs: number }[] = [];
    for (const name of names) {
      const id = name.endsWith('.sql') ? name.slice(0, -'.sql'.length) : '';
      if (!isValidDocId(id)) continue;
      try {
        result.push({ id, mtimeMs: (await fs.stat(path.join(dir, name))).mtimeMs });
      } catch {
        // Deleted meanwhile.
      }
    }
    return result;
  }

  // --- vscode.FileSystemProvider ---

  watch(): vscode.Disposable {
    // Only this extension writes the backing files, and it fires the events itself.
    return new vscode.Disposable(() => undefined);
  }

  async stat(uri: vscode.Uri): Promise<vscode.FileStat> {
    if (isSqlDocDirectory(uri.path)) return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };
    try {
      const s = await fs.stat(this.fileOf(uri));
      return { type: vscode.FileType.File, ctime: s.birthtimeMs, mtime: s.mtimeMs, size: s.size };
    } catch (err) {
      throw isNotFound(err) ? vscode.FileSystemError.FileNotFound(uri) : err;
    }
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    if (isSqlDocDirectory(uri.path)) throw vscode.FileSystemError.FileIsADirectory(uri);
    try {
      return await fs.readFile(this.fileOf(uri));
    } catch (err) {
      throw isNotFound(err) ? vscode.FileSystemError.FileNotFound(uri) : err;
    }
  }

  async writeFile(uri: vscode.Uri, content: Uint8Array, options: { readonly create: boolean; readonly overwrite: boolean }): Promise<void> {
    await this.write(uri, content, options);
  }

  async delete(uri: vscode.Uri): Promise<void> {
    if (isSqlDocDirectory(uri.path)) throw vscode.FileSystemError.NoPermissions(uri);
    const file = this.fileOf(uri);
    if (!(await exists(file))) throw vscode.FileSystemError.FileNotFound(uri);
    await fs.rm(file, { force: true });
    this.emitter.fire([{ type: vscode.FileChangeType.Deleted, uri }]);
  }

  readDirectory(uri: vscode.Uri): never {
    throw vscode.FileSystemError.NoPermissions(uri);
  }

  createDirectory(uri: vscode.Uri): never {
    throw vscode.FileSystemError.NoPermissions(uri);
  }

  rename(oldUri: vscode.Uri): never {
    throw vscode.FileSystemError.NoPermissions(oldUri);
  }

  dispose(): void {
    this.emitter.dispose();
    this.writes.dispose();
  }

  /** The one write path (editor saves and extension writes): flag check, write, change event, write event. */
  private async write(uri: vscode.Uri, content: Uint8Array, options: { readonly create: boolean; readonly overwrite: boolean }): Promise<void> {
    if (isSqlDocDirectory(uri.path)) throw vscode.FileSystemError.FileIsADirectory(uri);
    const file = this.fileOf(uri);
    const existed = await exists(file);
    const check = writeFileCheck(existed, options);
    if (check === 'FileNotFound') throw vscode.FileSystemError.FileNotFound(uri);
    if (check === 'FileExists') throw vscode.FileSystemError.FileExists(uri);
    await fs.mkdir(path.dirname(file), { recursive: true });
    await fs.writeFile(file, content);
    this.emitter.fire([{ type: existed ? vscode.FileChangeType.Changed : vscode.FileChangeType.Created, uri }]);
    this.writes.fire(uri);
  }

  /** The backing file of a document uri; FileNotFound for any path that is not `/<kind>/<id>/<title>`. */
  private fileOf(uri: vscode.Uri): string {
    const addr = SqlDocFileSystem.address(uri);
    if (!addr) throw vscode.FileSystemError.FileNotFound(uri);
    return backingFile(this.storageRoot, addr.kind, addr.id);
  }
}

/**
 * `<globalStorage>/sqldocs/query-index.json`: the kept query documents. Every update re-reads the file (another window
 * may have changed it) and replaces it atomically; updates from this window are serialized.
 */
export class QueryIndexFile {
  private chain: Promise<unknown> = Promise.resolve();
  private readonly file: string;

  constructor(storageRoot: string, private readonly log: Logger) {
    this.file = path.join(storageRoot, SQL_DOCS_DIR, QUERY_INDEX_FILE);
  }

  async read(): Promise<QueryIndex> {
    try {
      return parseQueryIndex(await fs.readFile(this.file, 'utf8'));
    } catch (err) {
      if (!isNotFound(err)) this.log.debug('sqldocs', `Reading the query index failed: ${message(err)}`);
      return {};
    }
  }

  /** Applies `change` to the current index and writes it back. Failures are logged (debug) and never thrown. */
  update(change: (index: QueryIndex) => QueryIndex): Promise<void> {
    const next = this.chain.then(async () => {
      try {
        const updated = change(await this.read());
        await fs.mkdir(path.dirname(this.file), { recursive: true });
        const tmp = `${this.file}.${process.pid}.tmp`;
        await fs.writeFile(tmp, serializeQueryIndex(updated), 'utf8');
        await fs.rename(tmp, this.file);
      } catch (err) {
        this.log.debug('sqldocs', `Updating the query index failed: ${message(err)}`);
      }
    });
    this.chain = next;
    return next;
  }
}

async function exists(file: string): Promise<boolean> {
  try {
    await fs.access(file);
    return true;
  } catch {
    return false;
  }
}
