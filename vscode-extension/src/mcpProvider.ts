import * as vscode from 'vscode';
import { PROVIDER_ID } from './constants';
import { ConnectionStore } from './connections/store';
import { buildServerConnections, missingPasswordMessage, missingPasswords } from './connections/serverEnv';
import { resolveExePath } from './exe';

export { PROVIDER_ID };

/** Exposes the open connection profiles to VS Code's agent mode as one MCP stdio server. */
export class MssqlMcpServerProvider implements vscode.McpServerDefinitionProvider<vscode.McpStdioServerDefinition> {
  private readonly emitter = new vscode.EventEmitter<void>();
  readonly onDidChangeMcpServerDefinitions = this.emitter.event;

  constructor(private readonly context: vscode.ExtensionContext, private readonly store: ConnectionStore) {}

  refresh(): void { this.emitter.fire(); }

  dispose(): void { this.emitter.dispose(); }

  provideMcpServerDefinitions(): vscode.McpStdioServerDefinition[] {
    if (process.platform !== 'win32') return [];
    const exe = resolveExePath(this.context.extensionUri);
    if (!exe || !this.store.list().some(p => p.open)) return [];
    const version = this.context.extension.packageJSON.version as string;
    return [new vscode.McpStdioServerDefinition('MSSQL-MCP', exe, [], {}, version)];
  }

  // Secrets are attached here so they are read only when the server actually starts.
  async resolveMcpServerDefinition(def: vscode.McpStdioServerDefinition): Promise<vscode.McpStdioServerDefinition> {
    const cfg = vscode.workspace.getConfiguration('msSqlMcp');
    const profiles = this.store.list();
    const passwords = await this.store.passwords();
    const missing = missingPasswords(profiles, passwords);
    if (missing.length) {
      // Non-blocking: these profiles are omitted from the server, the rest still start.
      void vscode.window.showWarningMessage(
        `MSSQL-MCP: ${missing.length} connection(s) skipped. ${missing.map(missingPasswordMessage).join(' ')}`);
    }
    def.env = {
      MSSQL_CONNECTIONS: buildServerConnections(profiles, passwords),
      USE_INSIGHTS_LAYER: cfg.get<boolean>('insights', true) ? 'true' : 'false',
      MSSQL_ALLOW_ADHOC_CONNECTIONS: cfg.get<boolean>('allowAdhocConnections', false) ? 'true' : 'false',
    };
    return def;
  }
}
