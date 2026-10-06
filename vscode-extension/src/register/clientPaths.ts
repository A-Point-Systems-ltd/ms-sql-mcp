import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

export function cursorConfigPath(home: string = os.homedir()): string {
  return path.join(home, '.cursor', 'mcp.json');
}

export interface PathEnv { APPDATA?: string; LOCALAPPDATA?: string }

/**
 * The connections the user manages from Claude Desktop's connection manager view (MSSQL_MANAGED_CONNECTIONS_FILE).
 * Separate from the extension's connections.json, which the extension regenerates and the server never writes.
 */
export function managedConnectionsFilePath(env: PathEnv = process.env): string | undefined {
  return env.APPDATA ? path.join(env.APPDATA, 'APoint-ms-sql', 'connections.json') : undefined;
}

/**
 * Claude Desktop config locations. Mirrors Get-ClaudeDesktopConfigPaths in install-accessmcp.ps1:
 * the documented %APPDATA% file, plus the virtualised copy of an MSIX install
 * (%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude) when that directory already exists.
 * A second file is never created for a layout the machine does not have.
 */
export function claudeDesktopConfigPaths(env: PathEnv = process.env, fsApi: Pick<typeof fs, 'existsSync' | 'readdirSync'> = fs): string[] {
  const out: string[] = [];
  if (env.APPDATA) out.push(path.join(env.APPDATA, 'Claude', 'claude_desktop_config.json'));
  if (env.LOCALAPPDATA) {
    const pkgRoot = path.join(env.LOCALAPPDATA, 'Packages');
    let names: string[] = [];
    try { names = fsApi.readdirSync(pkgRoot) as string[]; } catch { /* no Packages dir */ }
    for (const n of names.filter(x => /^Claude_/i.test(x)).sort()) {
      const dir = path.join(pkgRoot, n, 'LocalCache', 'Roaming', 'Claude');
      if (fsApi.existsSync(dir)) out.push(path.join(dir, 'claude_desktop_config.json'));
    }
  }
  return [...new Set(out)];
}
