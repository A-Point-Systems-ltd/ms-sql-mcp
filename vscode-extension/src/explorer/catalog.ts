// Object-explorer catalog model and pure parsers for list_objects / describe_table / describe_view payloads.
// No 'vscode' import — unit-testable with plain Node. Shapes verified against MssqlMcp/Tools/*.cs.
import { pick } from '../client/parse';

export type CategoryId = 'tables' | 'views' | 'procedures' | 'tvfs' | 'scalars' | 'dbTriggers' | 'types' | 'security' | 'logins' | 'serverRoles' | 'users' | 'roles';
export type ChildFolderId = 'indexes' | 'foreignKeys' | 'triggers';

export interface CategoryDef {
  id: CategoryId;
  label: string;
  icon: string;
  listType?: string;
  scriptType?: string;
  parent?: CategoryId;
  hasData?: boolean;
  childFolders?: ChildFolderId[];
}

export interface ObjectRef { connection: string; scriptType: string; schema?: string; name: string; parent?: string }
type Ref = Omit<ObjectRef, 'connection'>;

export const CATEGORIES: CategoryDef[] = [
  { id: 'tables', label: 'Tables', icon: 'table', listType: 'Table', scriptType: 'Table', hasData: true, childFolders: ['indexes', 'foreignKeys', 'triggers'] },
  { id: 'views', label: 'Views', icon: 'eye', listType: 'View', scriptType: 'View', hasData: true, childFolders: ['indexes'] },
  { id: 'procedures', label: 'Stored Procedures', icon: 'symbol-method', listType: 'StoredProcedure', scriptType: 'StoredProcedure' },
  { id: 'tvfs', label: 'Table-Valued Functions', icon: 'symbol-function', listType: 'TableFunction', scriptType: 'TableFunction' },
  { id: 'scalars', label: 'Scalar Functions', icon: 'symbol-function', listType: 'ScalarFunction', scriptType: 'ScalarFunction' },
  { id: 'dbTriggers', label: 'Database Triggers', icon: 'zap', listType: 'DatabaseTrigger', scriptType: 'DatabaseTrigger' },
  { id: 'types', label: 'Types', icon: 'symbol-class', listType: 'Type', scriptType: 'Type' },
  { id: 'security', label: 'Security', icon: 'shield' },
  { id: 'logins', label: 'Logins', icon: 'person', listType: 'Login', scriptType: 'Login', parent: 'security' },
  { id: 'serverRoles', label: 'Server Roles', icon: 'organization', listType: 'ServerRole', scriptType: 'ServerRole', parent: 'security' },
  { id: 'users', label: 'Database Users', icon: 'person', listType: 'DatabaseUser', scriptType: 'DatabaseUser', parent: 'security' },
  { id: 'roles', label: 'Database Roles', icon: 'organization', listType: 'DatabaseRole', scriptType: 'DatabaseRole', parent: 'security' },
];

const asArray = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? v : []) as Record<string, unknown>[];
const str = (v: unknown): string | undefined => (typeof v === 'string' && v.length ? v : undefined);

/** Rows of a list_objects `data` array. Tables are "schema.name" strings; everything else is objects. */
export function parseObjectList(data: unknown, def: CategoryDef): { schema?: string; name: string; detail?: string }[] {
  const rows = Array.isArray(data) ? data : [];
  return rows.map(row => {
    if (typeof row === 'string') {
      const dot = row.indexOf('.');
      return dot > 0 ? { schema: row.slice(0, dot), name: row.slice(dot + 1) } : { name: row };
    }
    const r = row as Record<string, unknown>;
    const name = str(pick(r, 'name')) ?? '?';
    const schema = str(pick(r, 'schema'));
    const detail = def.parent === 'security' || def.id === 'types' || def.id === 'dbTriggers'
      ? [str(pick(r, 'type')) ?? str(pick(r, 'kind')), pick(r, 'isDisabled') === true ? 'disabled' : undefined].filter(Boolean).join(' · ') || undefined
      : undefined;
    return { ...(schema ? { schema } : {}), name, ...(detail ? { detail } : {}) };
  });
}

/**
 * Children of a describe_table `data` payload. `constraints` (PK/UQ) and `indexes` are disjoint on the
 * server (indexes excludes primary-key / unique-constraint indexes), so concatenating does not duplicate.
 * FKs and triggers are schema-scoped objects in the table's schema.
 */
export function parseTableChildren(d: unknown, schema: string, table: string): Record<ChildFolderId, Ref[]> {
  const r = (d ?? {}) as Record<string, unknown>;
  const parent = `${schema}.${table}`;
  const index = (row: Record<string, unknown>): Ref => ({ scriptType: 'Index', name: String(pick(row, 'name')), parent });
  return {
    indexes: [...asArray(pick(r, 'constraints')).map(index), ...asArray(pick(r, 'indexes')).map(index)],
    foreignKeys: asArray(pick(r, 'foreignKeys')).map(f => ({ scriptType: 'ForeignKey', schema: str(pick(f, 'schema')) ?? schema, name: String(pick(f, 'name')) })),
    triggers: asArray(pick(r, 'triggers')).map(t => ({ scriptType: 'TableTrigger', schema, name: String(pick(t, 'name')) })),
  };
}

export function parseViewIndexes(d: unknown, schema: string, view: string): Ref[] {
  return asArray(pick(d, 'indexes')).map(i => ({ scriptType: 'Index', name: String(pick(i, 'name')), parent: `${schema}.${view}` }));
}
