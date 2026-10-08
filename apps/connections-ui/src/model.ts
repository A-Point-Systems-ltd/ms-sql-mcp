// Pure model of the connections view (no DOM): wire types, field visibility, form <-> server input.
// Labels and help texts come from the VS Code extension's form so both forms read the same.

export {
  AUTH_OPTIONS, ENCRYPTION_HELP, ENCRYPTION_OPTIONS, TRUST_HELP,
  canBringOnline, databaseLabel, describeDatabaseList, isOnline, stateOf,
} from '../../../vscode-extension/src/connections/connectionFormModel';
import type { DatabaseInfo } from '../../../vscode-extension/src/connections/connectionFormModel';
export type { DatabaseInfo };
import type { AuthKind } from '../../../vscode-extension/src/connections/profile';
export type { AuthKind };

export type EncryptKind = 'mandatory' | 'optional' | 'strict';

/** connections_ui_list -> managed[] (server: ManagedConnectionView). */
export interface ManagedView {
  name: string;
  auth: AuthKind;
  server: string;
  database: string;
  user?: string | null;
  hasPassword: boolean;
  encrypt: EncryptKind;
  trustServerCertificate: boolean;
  readOnly: boolean;
  insights: boolean;
  rawConnectionString?: string | null;
  isOpen: boolean;
  error?: string | null;
}

export interface OtherView {
  name: string;
  source: string;
  dataSource: string;
  database?: string | null;
  readOnly: boolean;
  isOpen: boolean;
}

export interface ListResult {
  managed: ManagedView[];
  others: OtherView[];
  fileError?: string | null;
}

export interface SaveResult {
  success: boolean;
  errors: Record<string, string>;
  message?: string | null;
}

/** connections_ui_test / _list_databases / _bring_online (server: ManagedProbeResult). */
export interface ProbeResult {
  success: boolean;
  message: string;
  /** List databases: every database with its state. */
  databases?: DatabaseInfo[] | null;
  /** Test / bring online: the form database's state when it is not ONLINE, or the state after bringing it online. */
  databaseState?: string | null;
}

/** What the form edits and what the server's ManagedConnectionInput accepts. */
export interface FormInput {
  name: string;
  auth: AuthKind;
  server: string;
  database: string;
  user: string;
  /** Write-only: never pre-filled. Empty on edit keeps the saved password. */
  password: string;
  rawConnectionString: string;
  encrypt: EncryptKind;
  trustServerCertificate: boolean;
  readOnly: boolean;
  insights: boolean;
}

export type FieldKey = 'server' | 'database' | 'user' | 'password' | 'rawConnectionString' | 'encrypt' | 'trustServerCertificate';

const SERVER_AUTH: readonly AuthKind[] = ['windows', 'sql', 'entraInteractive', 'entraDefault'];

/** Auth kinds for which a field is shown (same rules as the extension's form). */
export const SHOWN_FOR: Readonly<Record<FieldKey, readonly AuthKind[]>> = {
  server: SERVER_AUTH,
  database: SERVER_AUTH,
  user: ['sql', 'entraInteractive'],
  password: ['sql'],
  rawConnectionString: ['raw'],
  encrypt: SERVER_AUTH,
  trustServerCertificate: SERVER_AUTH,
};

export function isShown(field: FieldKey, auth: AuthKind): boolean {
  return SHOWN_FOR[field].includes(auth);
}

/** Defaults match the extension: Windows auth, read-only, insights on, optional encryption, trust certificate. */
export function defaultInput(): FormInput {
  return {
    name: '', auth: 'windows', server: '', database: '', user: '', password: '', rawConnectionString: '',
    encrypt: 'optional', trustServerCertificate: true, readOnly: true, insights: true,
  };
}

export function viewToInput(v: ManagedView): FormInput {
  return {
    name: v.name, auth: v.auth, server: v.server ?? '', database: v.database ?? '', user: v.user ?? '', password: '',
    rawConnectionString: v.rawConnectionString ?? '', encrypt: v.encrypt, trustServerCertificate: v.trustServerCertificate,
    readOnly: v.readOnly, insights: v.insights,
  };
}

/** Fields hidden for the chosen auth are sent empty, so stale values (e.g. a password typed before switching) never leave the form. */
export function toServerInput(f: FormInput): FormInput {
  const keep = (field: FieldKey, value: string) => (isShown(field, f.auth) ? value.trim() : '');
  return {
    ...f,
    name: f.name.trim(),
    server: keep('server', f.server),
    database: keep('database', f.database),
    user: keep('user', f.user),
    password: isShown('password', f.auth) ? f.password : '',
    rawConnectionString: keep('rawConnectionString', f.rawConnectionString),
  };
}

/** One line for the list: "srv / db" or "raw connection string". */
export function describeTarget(v: ManagedView): string {
  return v.auth === 'raw' ? 'raw connection string' : `${v.server} / ${v.database}`;
}

/** Text added to the model's context after a change, so Claude knows what the user did. Never includes secrets. */
export function changeNote(kind: 'added' | 'updated' | 'removed', f: Pick<FormInput, 'name' | 'readOnly'> & Partial<FormInput>): string {
  if (kind === 'removed') return `The user removed the connection '${f.name}'.`;
  const target = f.auth === 'raw' ? 'a raw connection string' : `${f.server}/${f.database}`;
  return `The user ${kind} the connection '${f.name}' (${target}, ${f.readOnly ? 'read-only' : 'read-write'}). Call list_connections for the current list.`;
}

/** Tool results arrive as structuredContent or as JSON text content. */
export function parseToolResult<T>(result: { structuredContent?: unknown; content?: Array<{ type: string; text?: string }>; isError?: boolean }): T {
  if (result.structuredContent && typeof result.structuredContent === 'object') return result.structuredContent as T;
  const text = result.content?.find(c => c.type === 'text')?.text;
  if (text === undefined) throw new Error('The server returned no result.');
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error(text);
  }
  if (result.isError) {
    const msg = (parsed as { error?: string; message?: string })?.error ?? (parsed as { message?: string })?.message ?? text;
    throw new Error(msg);
  }
  return parsed as T;
}
