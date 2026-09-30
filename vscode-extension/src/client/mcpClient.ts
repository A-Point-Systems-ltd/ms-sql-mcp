/** Minimal contract the explorer and commands need from an MCP tool client. */
export interface McpClient {
  initialize(): Promise<void>;
  callTool(name: string, args?: Record<string, unknown>): Promise<unknown>;
  dispose(): void;
}
