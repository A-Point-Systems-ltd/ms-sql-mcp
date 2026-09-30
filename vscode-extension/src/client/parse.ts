// Pure parsing helpers for MssqlMcp tool responses.
// No 'vscode' import — this module is unit-testable with plain Node.
//
// The server returns tool results serialized in a mix of casings: anonymous C#
// objects keep their literal names (e.g. `success`, `name`, `sql`) while class
// results serialize as PascalCase (e.g. `Fields`, `Code`, `TableName`). All lookups
// here are therefore case-insensitive.

/** A parsed MCP `tools/call` result envelope. */
export interface McpToolResultEnvelope {
  content?: Array<{ type?: string; text?: string }>;
  isError?: boolean;
}

export class McpToolError extends Error {}

/** Case-insensitive property lookup over a plain object. */
export function pick(obj: unknown, ...keys: string[]): unknown {
  if (!obj || typeof obj !== 'object') {
    return undefined;
  }
  const record = obj as Record<string, unknown>;
  const lowerWanted = keys.map((k) => k.toLowerCase());
  for (const actualKey of Object.keys(record)) {
    if (lowerWanted.includes(actualKey.toLowerCase())) {
      return record[actualKey];
    }
  }
  return undefined;
}

export function pickString(obj: unknown, ...keys: string[]): string | undefined {
  const value = pick(obj, ...keys);
  return typeof value === 'string' ? value : undefined;
}

/**
 * Unwrap a `tools/call` envelope to the underlying service payload. Throws
 * McpToolError when the call errored or the payload reports `success: false`.
 */
export function unwrapToolResult(envelope: unknown): unknown {
  const env = envelope as McpToolResultEnvelope | undefined;
  const textPart = env?.content?.find((c) => c.type === 'text' && typeof c.text === 'string');
  const text = textPart?.text;

  if (typeof text !== 'string') {
    if (env?.isError) {
      throw new McpToolError('Tool call failed with no message.');
    }
    // Some results (e.g. plain acknowledgements) may carry no text content.
    return undefined;
  }

  let payload: unknown;
  try {
    payload = JSON.parse(text);
  } catch {
    // Non-JSON text content (e.g. an error string). Surface it.
    if (env?.isError) {
      throw new McpToolError(text);
    }
    return text;
  }

  const success = pick(payload, 'success');
  if (success === false) {
    const message = errorText(payload) ?? 'Tool reported failure.';
    throw new McpToolError(message);
  }
  if (env?.isError) {
    const message = errorText(payload) ?? text;
    throw new McpToolError(message);
  }
  return payload;
}

/**
 * The failure text of a service result. Keys are tried in priority order and each one
 * separately, because results such as SqlQueryResult carry `message: null` alongside
 * the real `errorMessage`, and a single `pick` would stop at the null.
 */
function errorText(payload: unknown): string | undefined {
  for (const key of ['error', 'errorMessage', 'message']) {
    const value = pickString(payload, key);
    if (value) {
      return value;
    }
  }
  return undefined;
}
