// IntelliSense for SQL editors bound to a connection: request building, response validation, kind mapping, the
// debounce and warm-up decisions. The server side is the runner's extension-only `language_service` tool.
// No 'vscode' import — unit-testable with plain Node; intellisenseProviders.ts is the vscode glue.
import { pick } from '../client/parse';
import type { ConnectionProfile } from '../connections/profile';
import { findProfile } from './editorState';
import type { QueryAssociation } from './queryDocuments';

/** The runner tool that serves completion, hover, signature help, warm and refresh. */
export const LANGUAGE_SERVICE_TOOL = 'language_service';

export const COMPLETION_TRIGGER_CHARACTERS: readonly string[] = Object.freeze(['.', ' ', '(', ',', '@', '[']);
export const SIGNATURE_TRIGGER_CHARACTERS: readonly string[] = Object.freeze(['(', ',']);
export const SIGNATURE_RETRIGGER_CHARACTERS: readonly string[] = Object.freeze([',']);

/** Per-call timeout of every language_service call. */
export const LANGUAGE_SERVICE_TIMEOUT_MS = 5000;

/** Typing-triggered requests wait this long; a further keystroke cancels them before anything is sent. */
export const TYPING_DEBOUNCE_MS = 150;

/**
 * An "invoke" completion this soon after an edit of the document is a quick suggestion while typing (VS Code reports
 * those as invoke too), not an explicit Ctrl+Space.
 */
export const TYPING_WINDOW_MS = 100;

/** `warm` is sent at most once per connection target in this interval. */
export const WARM_INTERVAL_MS = 5 * 60 * 1000;

/** The setting that turns the providers and the warm-up off. */
export const INTELLISENSE_SETTING = 'intellisense.enabled';

/** Names of `vscode.CompletionItemKind` members the server kinds map to. */
export type CompletionKindName =
  | 'Class' | 'Interface' | 'Field' | 'Method' | 'Function' | 'Keyword' | 'Module' | 'Property' | 'Variable' | 'Folder'
  | 'TypeParameter' | 'Snippet' | 'Text';

const KIND_NAMES: Readonly<Record<string, CompletionKindName>> = Object.freeze({
  table: 'Class',
  view: 'Interface',
  column: 'Field',
  procedure: 'Method',
  function: 'Function',
  keyword: 'Keyword',
  schema: 'Module',
  parameter: 'Property',
  variable: 'Variable',
  database: 'Folder',
  type: 'TypeParameter',
  snippet: 'Snippet',
  other: 'Text',
});

/** The CompletionItemKind name for a server kind (case-insensitive); unknown kinds are Text. */
export function completionKindName(kind: unknown): CompletionKindName {
  return (typeof kind === 'string' && KIND_NAMES[kind.toLowerCase()]) || 'Text';
}

export type PositionAction = 'completion' | 'hover' | 'signatureHelp';

/**
 * The language_service arguments for a caret position. `text` is the whole document (never the selection); VS Code's
 * 0-based line and character become the server's 1-based line and column.
 */
export function buildPositionRequest(
  action: PositionAction, text: string, position: { line: number; character: number },
): { action: PositionAction; text: string; line: number; column: number } {
  return { action, text, line: position.line + 1, column: position.character + 1 };
}

export type CompletionTrigger = 'invoke' | 'triggerCharacter' | 'incomplete';

/** Whether a completion request is an explicit Ctrl+Space (not debounced) rather than typing-triggered. */
export function isExplicitCompletion(trigger: CompletionTrigger, msSinceLastEdit: number | undefined): boolean {
  if (trigger !== 'invoke') return false;
  return msSinceLastEdit === undefined || msSinceLastEdit > TYPING_WINDOW_MS;
}

/** How long a request waits before it is sent: 0 when explicit, else {@link TYPING_DEBOUNCE_MS}. */
export function requestDelayMs(explicit: boolean): number {
  return explicit ? 0 : TYPING_DEBOUNCE_MS;
}

/**
 * The server cache a profile warms: one per connection name, server and database (case-insensitive). A profile edited
 * to another database is another key.
 */
export function warmKey(profile: ConnectionProfile): string {
  return [profile.name, profile.auth, profile.server, profile.database].map(s => (s ?? '').toLowerCase()).join('\u0000');
}

/** Remembers when each key was warmed: `warm` is sent at most once per key per {@link WARM_INTERVAL_MS}. */
export class WarmTracker {
  private readonly last = new Map<string, number>();

  /** True (and recorded) when `key` was not warmed in the last interval. */
  shouldWarm(key: string, now: number): boolean {
    const at = this.last.get(key);
    if (at !== undefined && now - at < WARM_INTERVAL_MS) return false;
    this.last.set(key, now);
    return true;
  }

  /** Records `key` as warmed at `now` (a refresh rebuilds the cache too). */
  mark(key: string, now: number): void {
    this.last.set(key, now);
  }

  /** Forgets every key (the runner process restarted, so the server caches are empty). */
  clear(): void {
    this.last.clear();
  }
}

export interface LsCompletionItem {
  label: string;
  kind: string;
  detail?: string;
  insertText: string;
  sortText?: string;
}

export interface LsCompletionList {
  items: LsCompletionItem[];
  isIncomplete: boolean;
  cacheState: 'warm' | 'loading';
}

const optString = (v: unknown): string | undefined => (typeof v === 'string' ? v : undefined);
const nonEmpty = (v: unknown): string | undefined => (typeof v === 'string' && v.length > 0 ? v : undefined);

function completionItem(raw: unknown): LsCompletionItem | undefined {
  const label = nonEmpty(pick(raw, 'label'));
  if (!label) return undefined;
  const kind = optString(pick(raw, 'kind'));
  return {
    label,
    kind: kind && Object.prototype.hasOwnProperty.call(KIND_NAMES, kind.toLowerCase()) ? kind.toLowerCase() : 'other',
    detail: optString(pick(raw, 'detail')),
    insertText: nonEmpty(pick(raw, 'insertText')) ?? label,
    sortText: optString(pick(raw, 'sortText')),
  };
}

/**
 * The completion list of a language_service payload (`{ success, data: { items, isIncomplete, cacheState } }`).
 * Items without a non-empty string label are dropped; undefined when the payload has no item array.
 */
export function parseCompletionResult(payload: unknown): LsCompletionList | undefined {
  const data = pick(payload, 'data');
  const items = pick(data, 'items');
  if (!Array.isArray(items)) return undefined;
  const parsed: LsCompletionItem[] = [];
  for (const raw of items) {
    const item = completionItem(raw);
    if (item) parsed.push(item);
  }
  return {
    items: parsed,
    isIncomplete: pick(data, 'isIncomplete') === true,
    cacheState: pick(data, 'cacheState') === 'loading' ? 'loading' : 'warm',
  };
}

/** A 1-based, end-exclusive range (the server's TextRange). */
export interface LsRange { startLine: number; startColumn: number; endLine: number; endColumn: number }

const positiveInt = (v: unknown): number | undefined => (typeof v === 'number' && Number.isInteger(v) && v >= 1 ? v : undefined);

function range(raw: unknown): LsRange | undefined {
  const startLine = positiveInt(pick(raw, 'startLine'));
  const startColumn = positiveInt(pick(raw, 'startColumn'));
  const endLine = positiveInt(pick(raw, 'endLine'));
  const endColumn = positiveInt(pick(raw, 'endColumn'));
  if (startLine === undefined || startColumn === undefined || endLine === undefined || endColumn === undefined) return undefined;
  return { startLine, startColumn, endLine, endColumn };
}

/** Hover contents (plain text) and its optional range; undefined when there is nothing to show. */
export function parseHoverResult(payload: unknown): { contents: string; range?: LsRange } | undefined {
  const data = pick(payload, 'data');
  const contents = nonEmpty(pick(data, 'contents'));
  if (!contents) return undefined;
  return { contents, range: range(pick(data, 'range')) };
}

export interface LsSignature {
  label: string;
  documentation?: string;
  parameters: { label: string; documentation?: string }[];
}

export interface LsSignatureHelp {
  signatures: LsSignature[];
  activeSignature: number;
  /** 0-based; -1 when the caret is on no parameter. */
  activeParameter: number;
}

function signature(raw: unknown): LsSignature | undefined {
  const label = nonEmpty(pick(raw, 'label'));
  if (!label) return undefined;
  const params = pick(raw, 'parameters');
  const parameters: LsSignature['parameters'] = [];
  for (const p of Array.isArray(params) ? params : []) {
    const pLabel = nonEmpty(pick(p, 'label'));
    if (pLabel) parameters.push({ label: pLabel, documentation: optString(pick(p, 'documentation')) });
  }
  return { label, documentation: optString(pick(raw, 'documentation')), parameters };
}

/** Signature help with valid signatures only; undefined when there are none. */
export function parseSignatureHelpResult(payload: unknown): LsSignatureHelp | undefined {
  const data = pick(payload, 'data');
  const raw = pick(data, 'signatures');
  if (!Array.isArray(raw)) return undefined;
  const signatures = raw.map(signature).filter((s): s is LsSignature => s !== undefined);
  if (!signatures.length) return undefined;
  const active = pick(data, 'activeSignature');
  const activeSignature = typeof active === 'number' && Number.isInteger(active) && active >= 0 && active < signatures.length ? active : 0;
  const param = pick(data, 'activeParameter');
  const activeParameter = typeof param === 'number' && Number.isInteger(param) && param >= 0 ? param : -1;
  return { signatures, activeSignature, activeParameter };
}

const WORD_CHAR = /[A-Za-z0-9_$#@À-￿]/;

/**
 * Where the completion's replace range starts on the caret's line (0-based character): the identifier being typed,
 * including a leading `@`, `@@` or `#`, and an opening `[` before it. VS Code's default word range would leave those
 * out, so `@x = ` would be inserted after an `@` already typed.
 */
export function replaceStart(lineText: string, character: number): number {
  let start = Math.min(character, lineText.length);
  const caret = start;
  while (start > 0 && WORD_CHAR.test(lineText[start - 1])) start--;
  // Inside an unclosed bracketed name (`[My Ta|`), which may hold spaces: from its `[`.
  const open = lineText.lastIndexOf('[', start - 1);
  if (open >= 0 && !lineText.slice(open, caret).includes(']')) return open;
  return start;
}

/**
 * Keys (`uri.toString()`) of the documents IntelliSense serves: bound to a connection that exists and is open
 * (the editor/title key `resource in msSqlMcp.intellisenseDocs`).
 */
export function intellisenseDocs(
  entries: readonly (readonly [string, QueryAssociation])[], profiles: readonly ConnectionProfile[],
): string[] {
  return entries.filter(([, assoc]) => findProfile(profiles, assoc.connection)?.open === true).map(([key]) => key);
}

/** The `msSqlMcp.intellisense.enabled` value: only an explicit `false` turns IntelliSense off. */
export function intellisenseEnabled(value: unknown): boolean {
  return value !== false;
}

/** The database a profile's IntelliSense reads; a raw connection string profile is named by its connection. */
function databaseOf(profile: ConnectionProfile): string {
  return profile.auth === 'raw' || !profile.database ? profile.name : profile.database;
}

/** Status bar note while the server is still building the metadata cache (the list then has keywords only). */
export function loadingMessage(profile: ConnectionProfile): string {
  return `APoint-ms-sql: loading IntelliSense for ${databaseOf(profile)}…`;
}

/** Shown after Refresh IntelliSense Cache. */
export function refreshedMessage(profile: ConnectionProfile): string {
  const target = profile.auth === 'raw' || !profile.server ? profile.name : `${profile.server}/${profile.database}`;
  return `IntelliSense cache refreshed for ${target}`;
}
