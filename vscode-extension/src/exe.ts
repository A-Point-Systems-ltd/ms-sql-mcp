import * as fs from 'fs';
import * as vscode from 'vscode';

export const EXE_NAME = 'MssqlMcp.exe';
const CONFIG_SECTION = 'msSqlMcp';

export type ExeResolution =
  | { ok: true; path: string; source: 'setting' | 'bundled' }
  | { ok: false; reason: string };

/**
 * Locate the server executable.
 *
 * `msSqlMcp.serverPath` wins when set, which lets an estate deploy one shared
 * binary (see docs/INSTALL-SERVER.md) instead of a ~107 MB copy per user. When it is
 * empty we fall back to the copy bundled inside the .vsix.
 */
export function resolveExe(extensionUri: vscode.Uri): ExeResolution {
  const configured = (
    vscode.workspace.getConfiguration(CONFIG_SECTION).get<string>('serverPath') ?? ''
  ).trim();

  if (configured) {
    // Misconfiguration must be loud: silently falling back to the bundled exe would
    // hide the fact that the shared binary the admin intended is not being used.
    if (!fs.existsSync(configured)) {
      return {
        ok: false,
        reason: `msSqlMcp.serverPath points at a file that does not exist: ${configured}`,
      };
    }
    return { ok: true, path: configured, source: 'setting' };
  }

  const bundled = vscode.Uri.joinPath(extensionUri, 'bin', EXE_NAME).fsPath;
  if (fs.existsSync(bundled)) {
    return { ok: true, path: bundled, source: 'bundled' };
  }
  return {
    ok: false,
    reason:
      `Bundled ${EXE_NAME} was not found in the extension, and no msSqlMcp.serverPath is set.`,
  };
}

/** Convenience wrapper: the resolved path, or undefined when unavailable. */
export function resolveExePath(extensionUri: vscode.Uri): string | undefined {
  const resolved = resolveExe(extensionUri);
  return resolved.ok ? resolved.path : undefined;
}
