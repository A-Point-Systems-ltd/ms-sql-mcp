// Reading and writing `<globalStorage>/sqldocs/query-index.json`. No 'vscode' import: the file system is injected, so
// the merge, atomic write, quarantine and error paths are unit-tested.
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { QueryIndex, QueryIndexFormatError, parseQueryIndex, serializeQueryIndex } from './queryIndex';

/** The file operations the index needs (node:fs/promises in the extension, an in-memory fake in tests). */
export interface IndexFs {
  readFile(file: string): Promise<string>;
  writeFile(file: string, data: string): Promise<void>;
  rename(from: string, to: string): Promise<void>;
  mkdir(dir: string): Promise<void>;
}

export const nodeIndexFs: IndexFs = {
  readFile: file => fs.readFile(file, 'utf8'),
  writeFile: (file, data) => fs.writeFile(file, data, 'utf8'),
  rename: (from, to) => fs.rename(from, to),
  mkdir: async dir => { await fs.mkdir(dir, { recursive: true }); },
};

const isNotFound = (err: unknown): boolean => (err as NodeJS.ErrnoException | undefined)?.code === 'ENOENT';

/** Why an index operation failed, without any file content (codes and the format error only). */
function reason(err: unknown): string {
  if (err instanceof QueryIndexFormatError) return 'the file is not a valid index';
  const code = (err as NodeJS.ErrnoException | undefined)?.code;
  return code ? `error ${code}` : (err instanceof Error ? err.name : 'unknown error');
}

/** Suffix of a moved-aside damaged index: `.bad-<yyyyMMddHHmmss>` (local time). */
export function badIndexSuffix(date: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `.bad-${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}`
    + `${p(date.getHours())}${p(date.getMinutes())}${p(date.getSeconds())}`;
}

export interface QueryIndexFileOptions {
  /** Makes the temporary file name unique per process. */
  tmpSuffix?: string;
  /** Clock for the `.bad-<stamp>` name (tests). */
  now?: () => Date;
  /** Called after a damaged index was moved aside: the owner rebuilds it from the backing files. */
  onCorrupt?: () => void;
}

/**
 * The index file. `read()` is `{}` when the file does not exist. Non-empty text that is not an index is moved aside to
 * `query-index.json.bad-<stamp>` (kept, never deleted), reported with a warning and `onCorrupt`, and then reads as `{}`;
 * when it cannot be moved, read throws. Any other read error throws. `update()` re-reads the file each time (another
 * window may have changed it), serializes this window's updates, skips the write when `change` returns its input, and
 * replaces the file atomically (temporary file, then rename). When reading or writing fails it changes nothing, logs a
 * warning without content, and resolves false.
 */
export class QueryIndexFile {
  private chain: Promise<unknown> = Promise.resolve();
  private readonly tmpSuffix: string;
  private readonly now: () => Date;
  /** Set by the owner (see {@link QueryIndexFileOptions.onCorrupt}). */
  onCorrupt: (() => void) | undefined;

  constructor(
    private readonly file: string,
    private readonly fsys: IndexFs,
    private readonly warn: (message: string) => void,
    options: QueryIndexFileOptions = {},
  ) {
    this.tmpSuffix = options.tmpSuffix ?? String(process.pid);
    this.now = options.now ?? (() => new Date());
    this.onCorrupt = options.onCorrupt;
  }

  async read(): Promise<QueryIndex> {
    let text: string;
    try {
      text = await this.fsys.readFile(this.file);
    } catch (err) {
      if (isNotFound(err)) return {};
      throw err;
    }
    try {
      return parseQueryIndex(text);
    } catch (err) {
      if (!(err instanceof QueryIndexFormatError)) throw err;
      const bad = this.file + badIndexSuffix(this.now());
      // Throws (and the caller writes nothing) when the damaged file cannot be moved aside.
      await this.fsys.rename(this.file, bad);
      this.warn(`The query index was damaged; it was kept as ${path.basename(bad)} and is rebuilt from the saved query windows.`);
      this.onCorrupt?.();
      return {};
    }
  }

  update(change: (index: QueryIndex) => QueryIndex): Promise<boolean> {
    const next = this.chain.then(async () => {
      try {
        const current = await this.read();
        const updated = change(current);
        // No change (the same object back): nothing to write.
        if (updated === current) return true;
        await this.fsys.mkdir(path.dirname(this.file));
        const tmp = `${this.file}.${this.tmpSuffix}.tmp`;
        await this.fsys.writeFile(tmp, serializeQueryIndex(updated));
        await this.fsys.rename(tmp, this.file);
        return true;
      } catch (err) {
        this.warn(`Updating the query index failed (${reason(err)}); it was left unchanged.`);
        return false;
      }
    });
    this.chain = next;
    return next;
  }
}
