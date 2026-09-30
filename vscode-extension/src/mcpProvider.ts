import * as vscode from 'vscode';
import { PROVIDER_ID } from './constants';
import { ConnectionStore } from './connections/store';
import { AgentServerSettings, agentProviderEnv, buildServerConnections, definitionVersion, missingPasswordMessage, missingPasswords } from './connections/serverEnv';
import { resolveExePath } from './exe';

export { PROVIDER_ID };

function agentSettings(): AgentServerSettings {
  const cfg = vscode.workspace.getConfiguration('msSqlMcp');
  return {
    insights: cfg.get<boolean>('insights', true),
    allowAdhocConnections: cfg.get<boolean>('allowAdhocConnections', false),
    serverPath: cfg.get<string>('serverPath', ''),
  };
}

/** Exposes the open connection profiles to VS Code's agent mode as one MCP stdio server. */
export class MssqlMcpServerProvider implements vscode.McpServerDefinitionProvider<vscode.McpStdioServerDefinition> {
  private readonly emitter = new vscode.EventEmitter<void>();
  readonly onDidChangeMcpServerDefinitions = this.emitter.event;

  constructor(private readonly context: vscode.ExtensionContext, private readonly store: ConnectionStore) {}

  refresh(): void { this.emitter.fire(); }

  dispose(): void { this.emitter.dispose(); }

  async provideMcpServerDefinitions(): Promise<vscode.McpStdioServerDefinition[]> {
    if (process.platform !== 'win32') return [];
    const exe = resolveExePath(this.context.extensionUri);
    if (!exe) return [];
    // The server exits on an empty config, so offer it only when at least one open profile is usable.
    const profiles = this.store.list();
    const passwords = await this.store.passwords();
    const usable = profiles.filter(p => p.open).length - missingPasswords(profiles, passwords).length;
    if (usable <= 0) return [];
    // The version changes whenever the server config would, so VS Code restarts a running server on profile changes.
    const version = definitionVersion(this.context.extension.packageJSON.version as string, profiles, agentSettings(), passwords);
    return [new vscode.McpStdioServerDefinition('MSSQL-MCP', exe, [], {}, version)];
  }

  // Secrets are attached here so they are read only when the server actually starts.
  async resolveMcpServerDefinition(def: vscode.McpStdioServerDefinition): Promise<vscode.McpStdioServerDefinition> {
    const profiles = this.store.list();
    const passwords = await this.store.passwords();
    const missing = missingPasswords(profiles, passwords);
    if (missing.length) {
      // Non-blocking: these profiles are omitted from the server, the rest still start.
      void vscode.window.showWarningMessage(
        `MSSQL-MCP: ${missing.length} connection(s) skipped. ${missing.map(missingPasswordMessage).join(' ')}`);
    }
    def.env = agentProviderEnv(buildServerConnections(profiles, passwords), agentSettings());
    return def;
  }
}
