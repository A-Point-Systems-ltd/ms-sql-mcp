import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { Logger } from '../logger';
import {
  SQL_DOC_SCHEME, SqlDocAddress, SqlDocKind, backingFile, docTitle, parseSqlDocPath, sqlDocPath, TitleTarget,
} from './sqlDocNames';

const isNotFound = (err: unknown): boolean => (err as NodeJS.ErrnoException | undefined)?.code === 'ENOENT';

/**
 * The `mssql-sql:` file system: `mssql-sql:/<kind>/<id>/<title>.sql` is backed by
 * `<globalStorage>/sqldocs/<kind>/<id>.sql`. Only kind + id find the file; the title is the tab label. The extension changes a document's content only by writing
 * here and firing `onDidChangeFile` (the editor reloads a clean document), never with a programmatic text edit, so
 * Cursor shows no Keep / Undo review for it. Rename and directory operations are refused.
 */
export class SqlDocFileSystem implements vscode.FileSystemProvider, vscode.Disposable {
  private readonly emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile = this.emitter.event;

  /** @param storageRoot the extension's global storage folder (`context.globalStorageUri.fsPath`). */
  constructor(private readonly storageRoot: string, private readonly log: Logger) {}

  /** The document uri of `kind`/`id` with the tab title `<objectName> - <server> - <database>`. */
  static uri(kind: SqlDocKind, id: string, objectName: string, target: TitleTarget): vscode.Uri {
    return vscode.Uri.from({ scheme: SQL_DOC_SCHEME, path: sqlDocPath({ kind, id, title: docTitle(objectName, target) }) });
  }

  /** The address of a `mssql-sql:` uri, or undefined for another scheme or a malformed path. */
  static address(uri: vscode.Uri): SqlDocAddress | undefined {
    return uri.scheme === SQL_DOC_SCHEME ? parseSqlDocPath(uri.path) : undefined;
  }

  /** Writes `content` as the document's text and tells the editor it changed (a clean open document reloads). */
  async writeContent(uri: vscode.Uri, content: string): Promise<void> {
    const file = this.fileOf(uri);
    const existed = await exists(file);
    await fs.mkdir(path.dirname(file), { recursive: true });
    await fs.writeFile(file, content, 'utf8');
    this.emitter.fire([{ type: existed ? vscode.FileChangeType.Changed : vscode.FileChangeType.Created, uri }]);
  }

  /** Deletes the backing file of `kind`/`id` (best-effort, silent when it is already gone). */
  async deleteBacking(kind: SqlDocKind, id: string): Promise<void> {
    try {
      await fs.rm(backingFile(this.storageRoot, kind, id), { force: true });
    } catch (err) {
      this.log.debug('sqldocs', `Deleting a ${kind} document's backing file failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  // --- vscode.FileSystemProvider ---

  watch(): vscode.Disposable {
    // Only this extension writes the backing files, and it fires the events itself.
    return new vscode.Disposable(() => undefined);
  }

  async stat(uri: vscode.Uri): Promise<vscode.FileStat> {
    if (isVirtualDirectory(uri.path)) return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };
    try {
      const s = await fs.stat(this.fileOf(uri));
      return { type: vscode.FileType.File, ctime: s.birthtimeMs, mtime: s.mtimeMs, size: s.size };
    } catch (err) {
      throw isNotFound(err) ? vscode.FileSystemError.FileNotFound(uri) : err;
    }
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    try {
      return await fs.readFile(this.fileOf(uri));
    } catch (err) {
      throw isNotFound(err) ? vscode.FileSystemError.FileNotFound(uri) : err;
    }
  }

  async writeFile(uri: vscode.Uri, content: Uint8Array, options: { readonly create: boolean; readonly overwrite: boolean }): Promise<void> {
    const file = this.fileOf(uri);
    const existed = await exists(file);
    if (!existed && !options.create) throw vscode.FileSystemError.FileNotFound(uri);
    if (existed && options.create && !options.overwrite) throw vscode.FileSystemError.FileExists(uri);
    await fs.mkdir(path.dirname(file), { recursive: true });
    await fs.writeFile(file, content);
    this.emitter.fire([{ type: existed ? vscode.FileChangeType.Changed : vscode.FileChangeType.Created, uri }]);
  }

  async delete(uri: vscode.Uri): Promise<void> {
    if (isVirtualDirectory(uri.path)) throw vscode.FileSystemError.NoPermissions(uri);
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
  }

  /** The backing file of a document uri; FileNotFound for any path that is not `/<kind>/<id>/<title>.sql`. */
  private fileOf(uri: vscode.Uri): string {
    const addr = SqlDocFileSystem.address(uri);
    if (!addr) throw vscode.FileSystemError.FileNotFound(uri);
    return backingFile(this.storageRoot, addr.kind, addr.id);
  }
}

/**
 * `/`, `/<kind>` and `/<kind>/<id>` stat as directories, so the editor's save path (which checks that the parent
 * exists) never tries to create one; they cannot be listed, created or deleted.
 */
function isVirtualDirectory(p: string): boolean {
  return /^\/(?:(?:query|object)(?:\/[0-9a-f]{8,40})?)?\/?$/.test(p);
}

async function exists(file: string): Promise<boolean> {
  try {
    await fs.access(file);
    return true;
  } catch {
    return false;
  }
}
