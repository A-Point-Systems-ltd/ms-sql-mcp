/** Per-call options of {@link McpClient.callTool}. */
export interface CallToolOptions {
  /** Overrides the client's default timeout (60 000 ms); `null` means no timeout. */
  timeoutMs?: number | null;
  /** Aborting it cancels the call (`notifications/cancelled`) and rejects with a cancelled McpToolError. */
  signal?: AbortSignal;
}

/** Minimal contract the explorer and commands need from an MCP tool client. */
export interface McpClient {
  initialize(): Promise<void>;
  callTool(name: string, args?: Record<string, unknown>, opts?: CallToolOptions): Promise<unknown>;
  dispose(): void;
}
