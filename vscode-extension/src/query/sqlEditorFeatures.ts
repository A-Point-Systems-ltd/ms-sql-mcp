// The SQL editor enhancements: snippets, the formatter request, the object under the cursor (Ctrl+F12 / Ctrl+3), the
// column picker and * expansion. No 'vscode' import — unit-testable with plain Node; sqlEditorCommands.ts is the glue.
import { pick } from '../client/parse';
import type { ObjectRef } from '../explorer/catalog';

/** `msSqlMcp.completion.enhanced`: JOIN / ON suggestions, aliases, the column picker, * expansion and snippets. */
export const ENHANCED_SETTING = 'completion.enhanced';

/** The context key the editor tab's menu uses to show the switch's current state. */
export const ENHANCED_CONTEXT_KEY = 'msSqlMcp.enhancedCompletions';

/** The runner tool that formats SQL (bound to no connection). */
export const FORMAT_TOOL = 'format_sql';

/** Only an explicit `false` turns the enhancements off. */
export function enhancedEnabled(value: unknown): boolean {
  return value !== false;
}

// ----- snippets -----

export interface SqlSnippet {
  prefix: string;
  /** VS Code snippet syntax ($1, ${1:name}, $0). */
  body: string;
  description: string;
}

/** Quick snippets: type the prefix, then Tab. New procedures and functions carry `--with encryption` ready to enable. */
export const SQL_SNIPPETS: readonly SqlSnippet[] = Object.freeze([
  { prefix: 'ssf', body: 'select top(100) * from $0', description: 'Select top 100 rows' },
  { prefix: 'scf', body: 'select count(*) from $0', description: 'Count rows' },
  { prefix: 'sw', body: 'select * from ${1:table} where $0', description: 'Select with a filter' },
  { prefix: 'ii', body: 'insert into ${1:table} (${2:columns})\nvalues ($0)', description: 'Insert values' },
  { prefix: 'is', body: 'insert into ${1:table} (${2:columns})\nselect $0', description: 'Insert from a select' },
  { prefix: 'uu', body: 'update ${1:table}\nset ${2:column} = ${3:value}\nwhere $0', description: 'Update rows' },
  { prefix: 'df', body: 'delete from ${1:table}\nwhere $0', description: 'Delete rows' },
  { prefix: 'ij', body: 'inner join ${1:table} on $0', description: 'Inner join' },
  { prefix: 'lj', body: 'left join ${1:table} on $0', description: 'Left join' },
  { prefix: 'gb', body: 'group by $0', description: 'Group by' },
  { prefix: 'ob', body: 'order by $0', description: 'Order by' },
  { prefix: 'ex', body: 'exec ${1:procedure} $0', description: 'Execute a procedure' },
  { prefix: 'bt', body: 'begin tran\n\n$0\n\n--commit\nrollback', description: 'Transaction that rolls back until you switch to commit' },
  { prefix: 'tc', body: 'begin try\n    $0\nend try\nbegin catch\n    throw;\nend catch', description: 'TRY...CATCH block' },
  {
    prefix: 'cp',
    body: 'create or alter proc ${1:dbo}.${2:procName}(\n    ${3:@param int}\n    )\n--with encryption\nas\nbegin\n    set nocount on;\n\n    $0\nend\ngo',
    description: 'New stored procedure',
  },
  {
    prefix: 'cf',
    body: 'create or alter function ${1:dbo}.${2:fnName}(\n    ${3:@param int}\n    )\nreturns ${4:int}\n--with encryption\nas\nbegin\n    return $0\nend\ngo',
    description: 'New scalar function',
  },
  {
    prefix: 'ctf',
    body: 'create or alter function ${1:dbo}.${2:fnName}(\n    ${3:@param int}\n    )\nreturns table\n--with encryption\nas\nreturn (\n    select $0\n)\ngo',
    description: 'New inline table-valued function',
  },
  {
    prefix: 'dc',
    body: 'declare ${1:csr} cursor fast_forward for\n    select ${2:columns}\n    from ${3:table}\n\nopen $1\nfetch next from $1 into ${4:@variables}\nwhile @@fetch_status = 0\nbegin\n    $0\n    fetch next from $1 into $4\nend\nclose $1\ndeallocate $1',
    description: 'Cursor loop',
  },
]);

/** The snippet's text without placeholders, as a preview. */
export function snippetPreview(body: string): string {
  return body
    .replace(/\$\{\d+:([^}]*)\}/g, '$1')
    .replace(/\$\d+/g, '')
    .replace(/\\([$}\\])/g, '$1');
}

/** The hint beside the snippet in the suggest list: it expands with Tab. */
export function snippetHint(snippet: SqlSnippet): string {
  const firstLine = snippetPreview(snippet.body).split('\n')[0].trim();
  return `⇥ Tab → ${firstLine}`;
}

// ----- formatting -----

export type KeywordCase = 'lower' | 'upper' | 'preserve';

export interface FormatSettings {
  indentSize: number;
  useTabs: boolean;
  keywordCase: KeywordCase;
  maxItemsPerRow: number;
}

/** A 1-based, end-exclusive edit from format_sql. */
export interface LsEdit { startLine: number; startColumn: number; endLine: number; endColumn: number; newText: string }

/** The settings `msSqlMcp.format.*` with defaults and bounds (keyword case lower, 4 items per row). */
export function formatSettings(raw: { keywordCase?: unknown; maxItemsPerRow?: unknown }, editor: { tabSize: unknown; insertSpaces: unknown }): FormatSettings {
  const keywordCase: KeywordCase = raw.keywordCase === 'upper' || raw.keywordCase === 'preserve' ? raw.keywordCase : 'lower';
  const items = typeof raw.maxItemsPerRow === 'number' && Number.isInteger(raw.maxItemsPerRow) ? Math.min(50, Math.max(1, raw.maxItemsPerRow)) : 4;
  const tab = typeof editor.tabSize === 'number' && Number.isInteger(editor.tabSize) ? Math.min(16, Math.max(1, editor.tabSize)) : 4;
  return { indentSize: tab, useTabs: editor.insertSpaces === false, keywordCase, maxItemsPerRow: items };
}

/** format_sql arguments; `range` is 0-based (VS Code) and becomes 1-based. */
export function formatRequest(
  text: string, settings: FormatSettings,
  range?: { start: { line: number; character: number }; end: { line: number; character: number } },
): Record<string, unknown> {
  return {
    text, ...settings,
    ...(range ? {
      startLine: range.start.line + 1, startColumn: range.start.character + 1,
      endLine: range.end.line + 1, endColumn: range.end.character + 1,
    } : {}),
  };
}

const positiveInt = (v: unknown): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 1;

/** The edits of a successful format_sql payload; undefined when it holds none (malformed edits are dropped whole). */
export function parseFormatEdits(payload: unknown): LsEdit[] | undefined {
  const raw = pick(pick(payload, 'data'), 'edits');
  if (!Array.isArray(raw)) return undefined;
  const edits: LsEdit[] = [];
  for (const e of raw) {
    const startLine = pick(e, 'startLine'), startColumn = pick(e, 'startColumn'), endLine = pick(e, 'endLine'), endColumn = pick(e, 'endColumn');
    const newText = pick(e, 'newText');
    if (!positiveInt(startLine) || !positiveInt(startColumn) || !positiveInt(endLine) || !positiveInt(endColumn) || typeof newText !== 'string') return undefined;
    edits.push({ startLine, startColumn, endLine, endColumn, newText });
  }
  return edits;
}

// ----- the object under the cursor -----

const NAME_PART = String.raw`(?:\[[^\]]*(?:\]\][^\]]*)*\]|"[^"]*"|[A-Za-z_#][\w@#$]*)`;
const QUALIFIED_NAME = new RegExp(`${NAME_PART}(?:\\s*\\.\\s*${NAME_PART}){0,3}`, 'g');

/**
 * The object name to act on: the selection (one line; trailing arguments `(...)` and `;` dropped) or the qualified
 * name under the cursor (`dbo.T`, `[dbo].[My Table]`). Variables and empty text give undefined.
 */
export function objectNameAt(lineText: string, character: number, selection?: string): string | undefined {
  let name: string | undefined;
  if (selection && selection.trim()) {
    const s = selection.trim();
    if (/[\r\n]/.test(s)) return undefined;
    name = s.replace(/;+$/, '').replace(/\s*\(.*\)\s*$/, '').trim();
  } else {
    QUALIFIED_NAME.lastIndex = 0;
    for (let m = QUALIFIED_NAME.exec(lineText); m; m = QUALIFIED_NAME.exec(lineText)) {
      if (m.index <= character && character <= m.index + m[0].length) {
        // The tail of a variable (@x) is not an object name.
        if (lineText[m.index - 1] === '@') return undefined;
        name = m[0];
        break;
      }
    }
  }
  if (!name || name.startsWith('@') || name.length > 600) return undefined;
  return name.replace(/\s*\.\s*/g, '.');
}

export interface ObjectParameter { name: string; type: string; default?: string; isOutput: boolean }

export interface ObjectInfo {
  found: boolean;
  schema?: string;
  name?: string;
  type?: string;
  scriptType?: string;
  parameters: ObjectParameter[];
}

const optString = (v: unknown): string | undefined => (typeof v === 'string' && v.length ? v : undefined);

/** language_service objectInfo payload → ObjectInfo; undefined when malformed. */
export function parseObjectInfo(payload: unknown): ObjectInfo | undefined {
  const data = pick(payload, 'data');
  const found = pick(data, 'found');
  if (typeof found !== 'boolean') return undefined;
  const raw = pick(data, 'parameters');
  const parameters: ObjectParameter[] = (Array.isArray(raw) ? raw : []).flatMap(p => {
    const name = optString(pick(p, 'name'));
    const type = optString(pick(p, 'type'));
    if (!name || !type) return [];
    const def = optString(pick(p, 'default'));
    return [{ name, type, ...(def ? { default: def } : {}), isOutput: pick(p, 'isOutput') === true }];
  });
  return {
    found,
    schema: optString(pick(data, 'schema')), name: optString(pick(data, 'name')), type: optString(pick(data, 'type')),
    scriptType: optString(pick(data, 'scriptType')), parameters,
  };
}

/** The explorer's ObjectRef for a resolved object, or undefined when it has no script type here. */
export function objectRefOf(connection: string, info: ObjectInfo): ObjectRef | undefined {
  if (!info.found || !info.name || !info.scriptType) return undefined;
  return { connection, scriptType: info.scriptType, ...(info.schema ? { schema: info.schema } : {}), name: info.name };
}

/** A name in brackets unless it is a plain identifier. */
export function quoteName(name: string): string {
  return /^[A-Za-z_][\w@#$]*$/.test(name) ? name : `[${name.replace(/]/g, ']]')}]`;
}

/**
 * The query Ctrl+3 opens for a table-valued function: a DECLARE for each parameter without a default (fill them in),
 * DEFAULT for the others, and SELECT TOP (rows) * from the call. It is opened, not run.
 */
export function tvfQuery(schema: string | undefined, name: string, parameters: readonly ObjectParameter[], rows: number): string {
  const needed = parameters.filter(p => p.default === undefined);
  const declare = needed.length
    ? `declare ${needed.map(p => `${p.name} ${p.type} = null`).join('\n        ,')}\n\n`
    : '';
  const args = parameters.map(p => (p.default === undefined ? p.name : `default /* ${p.name} = ${p.default} */`)).join(', ');
  const target = schema ? `${quoteName(schema)}.${quoteName(name)}` : quoteName(name);
  return `${declare}select top (${rows}) *\nfrom ${target}(${args})\n`;
}

// ----- column picker and * expansion -----

export interface ScopeColumn { name: string; type: string; nullable: boolean; isKey: boolean }
export interface ScopeTable { alias?: string; schema?: string; name: string; kind?: string; columns: ScopeColumn[] }

/** language_service scope payload → tables; undefined when malformed. */
export function parseScope(payload: unknown): { tables: ScopeTable[]; loading: boolean } | undefined {
  const data = pick(payload, 'data');
  const raw = pick(data, 'tables');
  if (!Array.isArray(raw)) return undefined;
  const tables = raw.flatMap((t): ScopeTable[] => {
    const name = optString(pick(t, 'name'));
    if (!name) return [];
    const cols = pick(t, 'columns');
    const columns = (Array.isArray(cols) ? cols : []).flatMap((c): ScopeColumn[] => {
      const cn = optString(pick(c, 'name'));
      return cn ? [{ name: cn, type: optString(pick(c, 'type')) ?? '', nullable: pick(c, 'nullable') === true, isKey: pick(c, 'isKey') === true }] : [];
    });
    const alias = optString(pick(t, 'alias'));
    const schema = optString(pick(t, 'schema'));
    return [{ ...(alias ? { alias } : {}), ...(schema ? { schema } : {}), name, kind: optString(pick(t, 'kind')), columns }];
  });
  return { tables, loading: pick(data, 'cacheState') === 'loading' };
}

/** How a table's columns are qualified: its alias, else its name. */
export function qualifierOf(table: ScopeTable): string {
  return table.alias ?? table.name;
}

export interface PickerItem {
  label: string;
  description: string;
  /** The text inserted for this column. */
  insert: string;
}

/** One picker row per column; columns are qualified when the statement has more than one table or an alias. */
export function pickerItems(tables: readonly ScopeTable[]): PickerItem[] {
  const qualify = tables.length > 1 || tables.some(t => t.alias);
  return tables.flatMap(t => t.columns.map(c => {
    const insert = qualify ? `${quoteName(qualifierOf(t))}.${quoteName(c.name)}` : quoteName(c.name);
    const traits = [c.type, c.isKey ? 'key' : '', c.nullable ? 'null' : 'not null'].filter(Boolean).join(' · ');
    return { label: insert, description: traits, insert };
  }));
}

/** Where a `*` (or `alias.*`) of a SELECT list sits on the line, or undefined (count(*), multiplication, none). */
export function wildcardAt(lineText: string, character: number): { start: number; end: number; qualifier?: string } | undefined {
  const re = /(?:^|(?<=[\s,]))((?:\[[^\]]+\]|[A-Za-z_#][\w@#$]*)\.)?\*/g;
  for (let m = re.exec(lineText); m; m = re.exec(lineText)) {
    const start = m.index;
    const end = start + m[0].length;
    if (character < start || character > end) continue;
    const prefix = lineText.slice(0, start).trimEnd();
    const listStart = prefix === '' || /(?:\bselect|\bdistinct|,|\btop\s*\(\s*\d+\s*\)(?:\s*percent)?(?:\s*with\s+ties)?|\btop\s+\d+)$/i.test(prefix);
    if (!listStart) return undefined;
    const qualifier = m[1] ? m[1].slice(0, -1).replace(/^\[(.*)\]$/, '$1') : undefined;
    return { start, end, ...(qualifier ? { qualifier } : {}) };
  }
  return undefined;
}

/** The column list that replaces `*` / `qualifier.*`; undefined when no table (or no columns) match. */
export function expandWildcard(tables: readonly ScopeTable[], qualifier?: string): string | undefined {
  const chosen = qualifier
    ? tables.filter(t => qualifierOf(t).toLowerCase() === qualifier.toLowerCase() || t.name.toLowerCase() === qualifier.toLowerCase())
    : tables;
  if (!chosen.length || chosen.some(t => !t.columns.length)) return undefined;
  const qualify = !!qualifier || chosen.length > 1 || chosen.some(t => t.alias);
  return chosen.flatMap(t => t.columns.map(c => (qualify ? `${quoteName(qualifier ?? qualifierOf(t))}.${quoteName(c.name)}` : quoteName(c.name)))).join(', ');
}
