// Object-tree node model: node kinds, labels, contextValues, ids, script_object arguments and DDL text.
// No 'vscode' import — unit-testable with plain Node; explorerTree.ts maps ItemSpec onto vscode.TreeItem.
import { pick } from '../client/parse';
import type { ConnectionProfile } from '../connections/profile';
import { matchesFilter } from '../tree/filter';
import { CATEGORIES, CategoryDef, ChildFolderId, ObjectRef, parseTableChildren, parseViewIndexes } from './catalog';
import { previewSql, qualified } from './sqlText';

export interface ConnectionNode { kind: 'connection'; profile: ConnectionProfile }
export interface CategoryNode { kind: 'category'; connection: string; def: CategoryDef }
export interface ObjectNode { kind: 'object'; ref: ObjectRef; def: CategoryDef; detail?: string }
export interface ChildFolderNode { kind: 'childFolder'; connection: string; folder: ChildFolderId; parentRef: ObjectRef }
export interface ChildNode { kind: 'child'; ref: ObjectRef }
export interface MessageNode { kind: 'message'; text: string; isError: boolean; tooltip?: string }
export type ExplorerNode = ConnectionNode | CategoryNode | ObjectNode | ChildFolderNode | ChildNode | MessageNode;

/** Children of one table/view, by folder. */
export type ObjectChildren = Record<ChildFolderId, Omit<ObjectRef, 'connection'>[]>;

/** What explorerTree.ts turns into a vscode.TreeItem. */
export interface ItemSpec {
  id?: string;
  label: string;
  description?: string;
  tooltip?: string;
  contextValue: string;
  collapsible: boolean;
  /** Codicon id. */
  icon: string;
  /** Click command; it receives the node as its argument. */
  command?: 'msSqlMcp.showDdl';
}

export const CLOSED_MESSAGE = 'Closed - right-click › Open to browse';
export const EMPTY_MESSAGE = '(none)';
const SHOW_DDL = 'msSqlMcp.showDdl';

const FOLDERS: Record<ChildFolderId, { label: string; icon: string }> = {
  indexes: { label: 'Indexes', icon: 'list-tree' },
  foreignKeys: { label: 'Foreign Keys', icon: 'references' },
  triggers: { label: 'Triggers', icon: 'zap' },
};

const CHILD_ICONS: Record<string, string> = { Index: 'list-tree', ForeignKey: 'references', TableTrigger: 'zap' };

/** script_object types whose `name` is '[schema.]name'; the rest (principals, DB triggers, Index) take the plain name. */
const SCHEMA_SCOPED = new Set(['Table', 'View', 'ForeignKey', 'TableTrigger', 'StoredProcedure', 'TableFunction', 'ScalarFunction', 'Type']);

/** Server errors that only mean "the private process was restarted under this call". */
const TRANSIENT = /Explorer restarted|Client disposed/i;

/** Escapes one key/id component so '|' (separator) and '.' (schema.name) inside names cannot collide. */
const k = (v: string): string => encodeURIComponent(v).replace(/\./g, '%2E');

/** Prefix shared by every cache key of one connection. */
export const connectionPrefix = (connection: string): string => `${k(connection)}|`;
export const categoryKey = (connection: string, categoryId: string): string => `${connectionPrefix(connection)}${categoryId}`;
export const childrenKey = (connection: string, schema: string | undefined, name: string): string =>
  `${connectionPrefix(connection)}${k(schema ?? '')}.${k(name)}|children`;

const display = (ref: { schema?: string; name: string }): string => (ref.schema ? `${ref.schema}.${ref.name}` : ref.name);

export function connectionDescription(p: ConnectionProfile): string {
  const target = p.auth === 'raw' ? 'connection string' : `${p.server}/${p.database}`;
  return [target, p.readOnly ? 'read-only' : undefined, p.open ? undefined : 'closed'].filter(Boolean).join(' · ');
}

export function rootCategories(connection: string): CategoryNode[] {
  return CATEGORIES.filter(d => !d.parent).map(def => ({ kind: 'category', connection, def }));
}

export function subCategories(connection: string, parent: CategoryDef): CategoryNode[] {
  return CATEGORIES.filter(d => d.parent === parent.id).map(def => ({ kind: 'category', connection, def }));
}

/** Object nodes of one category after the name filter; `total` is the unfiltered count. */
export function objectNodes(connection: string, def: CategoryDef, rows: { schema?: string; name: string; detail?: string }[], term: string): { nodes: ObjectNode[]; total: number } {
  const nodes = rows
    .filter(r => matchesFilter(display(r), term))
    .map((r): ObjectNode => ({
      kind: 'object',
      def,
      ref: { connection, scriptType: def.scriptType ?? '', ...(r.schema ? { schema: r.schema } : {}), name: r.name },
      ...(r.detail ? { detail: r.detail } : {}),
    }));
  return { nodes, total: rows.length };
}

export function childFolderNodes(node: ObjectNode): ChildFolderNode[] {
  return (node.def.childFolders ?? []).map(folder => ({ kind: 'childFolder', connection: node.ref.connection, folder, parentRef: node.ref }));
}

/**
 * describe_table / describe_view payload -> children by folder. Index parents are re-quoted as
 * '[schema].[name]' so dotted names reach script_object intact.
 */
export function parseChildren(def: CategoryDef, data: unknown, schema: string, name: string): ObjectChildren {
  const parent = qualified(schema, name);
  const base: ObjectChildren = def.id === 'tables'
    ? parseTableChildren(data, schema, name)
    : { indexes: parseViewIndexes(data, schema, name), foreignKeys: [], triggers: [] };
  return {
    indexes: base.indexes.map(i => ({ ...i, parent })),
    foreignKeys: base.foreignKeys,
    triggers: base.triggers,
  };
}

export function childNodes(connection: string, folder: ChildFolderId, children: ObjectChildren): ChildNode[] {
  return children[folder].map(ref => ({ kind: 'child', ref: { connection, ...ref } }));
}

/**
 * read_data arguments for Data View: TOP (rows + 1) with maxRows = rows, so a table with more rows
 * makes the server cut at `rows` and report truncated=true.
 */
export function dataViewRequest(ref: ObjectRef, rows: number): { sql: string; maxRows: number } {
  return { sql: previewSql(ref.schema, ref.name, rows + 1), maxRows: rows };
}

export function scriptArgs(ref: ObjectRef): { objectType: string; name: string; parent?: string } {
  const name = ref.schema && SCHEMA_SCOPED.has(ref.scriptType) ? qualified(ref.schema, ref.name) : ref.name;
  return { objectType: ref.scriptType, name, ...(ref.parent ? { parent: ref.parent } : {}) };
}

/** script_object `data` -> document text. With warnings, a provenance header and the warnings come first. */
export function ddlText(result: unknown, connection: string): string {
  const ddl = String(pick(result, 'ddl') ?? '');
  const raw = pick(result, 'warnings');
  const warnings = (Array.isArray(raw) ? raw : []).map(w => String(w)).filter(w => w.length);
  if (!warnings.length) return ddl;
  const form = String(pick(result, 'form') ?? '');
  const lines = warnings.map(w => w.split(/\r?\n/).map((l, i) => (i === 0 ? `-- WARNING: ${l}` : `--   ${l}`)).join('\n'));
  return [`-- Generated by MSSQL-MCP (${form}) from ${connection}`, ...lines, '', ddl].join('\n');
}

export function errorNode(err: unknown): MessageNode {
  const full = err instanceof Error ? err.message : String(err);
  if (TRANSIENT.test(full)) return { kind: 'message', text: 'The explorer restarted - click Refresh to retry.', isError: true, tooltip: full };
  const first = full.split(/\r?\n/)[0].trim() || 'Unknown error.';
  return { kind: 'message', text: first.length > 200 ? `${first.slice(0, 199)}…` : first, isError: true, tooltip: full };
}

/** Stable tree id (keeps expansion state across refreshes). Message nodes have none. */
export function nodeId(node: ExplorerNode): string | undefined {
  const refId = (r: ObjectRef) => [r.connection, r.scriptType, r.parent ?? '', r.schema ?? '', r.name].map(k).join('|');
  switch (node.kind) {
    case 'connection': return `conn|${k(node.profile.name)}`;
    case 'category': return `cat|${k(node.connection)}|${node.def.id}`;
    case 'object': return `obj|${refId(node.ref)}`;
    case 'childFolder': return `folder|${refId(node.parentRef)}|${node.folder}`;
    case 'child': return `child|${refId(node.ref)}`;
    case 'message': return undefined;
  }
}

/** Item presentation per node kind. `counts` (category only) is shown as "n of m" while a filter is active. */
export function describeNode(node: ExplorerNode, counts?: { shown: number; total: number }): ItemSpec {
  const id = nodeId(node);
  switch (node.kind) {
    case 'connection': {
      const p = node.profile;
      return {
        id, label: p.name, description: connectionDescription(p), tooltip: `${p.name}: ${connectionDescription(p)}`,
        contextValue: p.open ? 'msSqlMcp.conn.open' : 'msSqlMcp.conn.closed', collapsible: true,
        icon: p.open ? 'database' : 'circle-slash',
      };
    }
    case 'category':
      return {
        id, label: node.def.label, ...(counts ? { description: `${counts.shown} of ${counts.total}` } : {}),
        contextValue: 'msSqlMcp.category', collapsible: true, icon: node.def.icon,
      };
    case 'object': {
      const hasChildren = !!node.def.childFolders?.length;
      const label = display(node.ref);
      return {
        id, label, ...(node.detail ? { description: node.detail } : {}), tooltip: `${node.def.label}: ${label}`,
        contextValue: node.def.id === 'tables' ? 'msSqlMcp.obj.table' : node.def.id === 'views' ? 'msSqlMcp.obj.view' : `msSqlMcp.obj.${node.ref.scriptType}`,
        collapsible: hasChildren, icon: node.def.icon, ...(hasChildren ? {} : { command: SHOW_DDL }),
      };
    }
    case 'childFolder':
      return { id, label: FOLDERS[node.folder].label, contextValue: 'msSqlMcp.folder', collapsible: true, icon: FOLDERS[node.folder].icon };
    case 'child':
      return {
        id, label: node.ref.name, tooltip: `${node.ref.scriptType}: ${display(node.ref)}`,
        contextValue: 'msSqlMcp.obj.child', collapsible: false, icon: CHILD_ICONS[node.ref.scriptType] ?? 'symbol-misc', command: SHOW_DDL,
      };
    case 'message':
      return {
        label: node.text, ...(node.tooltip ? { tooltip: node.tooltip } : {}),
        contextValue: 'msSqlMcp.message', collapsible: false, icon: node.isError ? 'error' : 'info',
      };
  }
}
