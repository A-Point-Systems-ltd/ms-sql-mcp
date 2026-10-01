// Pure JSON merge for MCP client config files (no vscode / fs imports).

export interface McpEntry { command: string; args: string[]; env: Record<string, string> }

/**
 * Returns the config text with `key` set under `mcpServers`, preserving every other key.
 * Throws (without any side effect) when the existing text is not a JSON object, so callers never overwrite a file they cannot parse.
 */
export function mergeMcpServer(existingText: string | undefined, key: string, entry: McpEntry): string {
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
  servers[key] = entry;
  root.mcpServers = servers;
  return JSON.stringify(root, null, 2) + '\n';
}
