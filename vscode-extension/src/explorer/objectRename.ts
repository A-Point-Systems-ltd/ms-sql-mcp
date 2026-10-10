// Copy Name and Rename for object-explorer items: the copied text, which objects can be renamed, the rename SQL and
// the dependency check. No 'vscode' import — unit-testable with plain Node; explorerCommands.ts is the glue.
import type { ObjectRef } from './catalog';

const q = (name: string) => `[${name.replace(/]/g, ']]')}]`;
const n = (text: string) => `N'${text.replace(/'/g, "''")}'`;

/** Script types renamed with sp_rename, and its @objtype for each. */
const SP_RENAME: Readonly<Record<string, string>> = Object.freeze({
  Table: 'OBJECT', View: 'OBJECT', StoredProcedure: 'OBJECT', TableFunction: 'OBJECT', ScalarFunction: 'OBJECT',
  TableTrigger: 'OBJECT', ForeignKey: 'OBJECT', Index: 'INDEX', Type: 'USERDATATYPE',
});

/** Principals renamed with ALTER ... WITH NAME. */
const ALTER_NAME: Readonly<Record<string, string>> = Object.freeze({
  DatabaseRole: 'ROLE', DatabaseUser: 'USER', Login: 'LOGIN', ServerRole: 'SERVER ROLE',
});

/** Modules whose stored definition keeps the old name after sp_rename (scripting them shows it). */
const MODULES = new Set(['View', 'StoredProcedure', 'TableFunction', 'ScalarFunction', 'TableTrigger']);

/** The text Copy Name puts on the clipboard: schema.name for schema-scoped objects, else the name. */
export function copyNameText(ref: Pick<ObjectRef, 'schema' | 'name'>): string {
  return ref.schema ? `${ref.schema}.${ref.name}` : ref.name;
}

/** Why the object cannot be renamed here, or undefined when it can. */
export function renameUnsupported(ref: ObjectRef): string | undefined {
  if (SP_RENAME[ref.scriptType] || ALTER_NAME[ref.scriptType]) {
    if (ref.scriptType === 'Index' && !ref.parent) return 'The index has no table.';
    return undefined;
  }
  return `Renaming ${ref.scriptType === 'DatabaseTrigger' ? 'database triggers' : `objects of type ${ref.scriptType}`} is not supported.`;
}

/** Why `name` is not a usable new name for `ref`, or undefined. */
export function validateNewName(ref: ObjectRef, name: string): string | undefined {
  if (!name.trim()) return 'Enter a name.';
  if (name !== name.trim()) return 'The name cannot start or end with spaces.';
  if (name.length > 128) return 'Names are at most 128 characters.';
  if (/[\u0000-\u001f]/.test(name)) return 'The name cannot contain control characters.';
  if (name === ref.name) return 'This is the current name.';
  return undefined;
}

/** The object's name as sp_rename's @objname: [schema].[name], or [schema].[table].[index] for an index. */
function objName(ref: ObjectRef): string {
  if (ref.scriptType === 'Index') return `${ref.parent}.${q(ref.name)}`;
  return ref.schema ? `${q(ref.schema)}.${q(ref.name)}` : q(ref.name);
}

/**
 * The rename statement. sp_rename takes the new name as plain text (no brackets: it becomes part of the name);
 * principals use ALTER ROLE / USER / LOGIN / SERVER ROLE ... WITH NAME = [new].
 */
export function renameSql(ref: ObjectRef, newName: string): string {
  const objtype = SP_RENAME[ref.scriptType];
  if (objtype) return `EXEC sys.sp_rename @objname = ${n(objName(ref))}, @newname = ${n(newName)}, @objtype = N'${objtype}';`;
  const kind = ALTER_NAME[ref.scriptType];
  if (kind) return `ALTER ${kind} ${q(ref.name)} WITH NAME = ${q(newName)};`;
  throw new Error(renameUnsupported(ref) ?? 'Not supported.');
}

/**
 * A read-only query listing up to 20 modules (views, procedures, functions, triggers) that reference the object by
 * name and would break after the rename. Undefined for objects such a dependency cannot point at (principals, indexes).
 */
export function dependentsSql(ref: ObjectRef): string | undefined {
  if (!SP_RENAME[ref.scriptType] || ref.scriptType === 'Index' || ref.scriptType === 'ForeignKey') return undefined;
  const target = ref.scriptType === 'Type'
    ? `d.referenced_class = 6 AND d.referenced_id = TYPE_ID(${n(objName(ref))})`
    : `d.referenced_class = 1 AND d.referenced_id = OBJECT_ID(${n(objName(ref))})`;
  return `SELECT DISTINCT TOP (20) OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id) AS name `
    + `FROM sys.sql_expression_dependencies d WHERE ${target} AND d.referencing_id <> ISNULL(OBJECT_ID(${n(objName(ref))}), 0) ORDER BY 1;`;
}

/** The confirmation text: what is renamed, what breaks, and the stored-definition caveat for modules. */
export function renameWarning(ref: ObjectRef, newName: string, dependents: readonly string[]): string {
  const lines = [`Rename ${copyNameText(ref)} to ${newName}?`];
  if (dependents.length) {
    lines.push(`${dependents.length >= 20 ? '20 or more' : dependents.length} object(s) reference it by name and will break: ${dependents.join(', ')}.`);
  }
  lines.push('Application code, jobs and scripts that use the old name will break too.');
  if (MODULES.has(ref.scriptType)) lines.push('Its stored definition keeps the old name in its text; script and re-apply it to update that.');
  return lines.join('\n\n');
}
