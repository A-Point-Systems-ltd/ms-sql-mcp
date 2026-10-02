// Pure JSON merge for MCP client config files (no vscode / fs imports).

export interface McpEntry { command: string; args: string[]; env: Record<string, string> }

/**
 * Returns the config text with `key` set under `mcpServers`, preserving every other key. `legacyKey`, when given, is
 * removed from `mcpServers` in the same result (the pre-rename server key); no other server is touched.
 * Throws (without any side effect) when the existing text is not a JSON object, so callers never overwrite a file they cannot parse.
 */
export function mergeMcpServer(existingText: string | undefined, key: string, entry: McpEntry, legacyKey?: string): string {
  let root: Record<string, unknown> = {};
  if (existingText && existingText.trim()) {
    let parsed: unknown;
    try {
      parsed = JSON.parse(existingText.replace(/^﻿/, ''));
    } catch {
      throw new Error('Existing config is not valid JSON; fix or remove it first (it was not modified).');
    }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error('Existing config is not valid JSON object; fix or remove it first (it was not modified).');
    }
    root = parsed as Record<string, unknown>;
  }
  const existing = root.mcpServers;
  const servers = (existing && typeof existing === 'object' && !Array.isArray(existing) ? existing : {}) as Record<string, unknown>;
  if (legacyKey && legacyKey !== key) delete servers[legacyKey];
  servers[key] = entry;
  root.mcpServers = servers;
  return JSON.stringify(root, null, 2) + '\n';
}

/** True when the config text parses to an object whose `mcpServers` has `key`. Unparseable text counts as false. */
export function hasServerKey(text: string | undefined, key: string): boolean {
  if (!text || !text.trim()) return false;
  try {
    const servers = (JSON.parse(text.replace(/^﻿/, '')) as { mcpServers?: unknown } | null)?.mcpServers;
    return typeof servers === 'object' && servers !== null && !Array.isArray(servers) && Object.prototype.hasOwnProperty.call(servers, key);
  } catch {
    return false;
  }
}
