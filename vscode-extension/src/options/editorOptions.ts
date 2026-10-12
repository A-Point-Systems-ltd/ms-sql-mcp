import * as vscode from 'vscode';
import type { Logger } from '../logger';
import { makeNonce } from '../webviewUtil';
import { renderEditorOptions } from './editorOptionsHtml';
import { OPTION_SETTINGS, parseOptionsMessage, settingsUpdates, valuesFromSettings } from './editorOptionsModel';

/**
 * Registers `msSqlMcp.editorOptions`: one form (reused while open) for the completion, formatting and query-shortcut
 * settings. It edits the real `msSqlMcp.*` settings, so settings.json stays the single source of truth.
 */
export function registerEditorOptions(context: vscode.ExtensionContext, log: Logger): void {
  let panel: vscode.WebviewPanel | undefined;
  const config = () => vscode.workspace.getConfiguration('msSqlMcp');

  const render = (target: vscode.WebviewPanel) => {
    target.webview.html = renderEditorOptions(valuesFromSettings(key => config().get(key)), {
      nonce: makeNonce(), hasWorkspace: !!vscode.workspace.workspaceFolders?.length,
    });
  };

  context.subscriptions.push(vscode.commands.registerCommand('msSqlMcp.editorOptions', () => {
    if (panel) {
      panel.reveal();
      return;
    }
    const opened = vscode.window.createWebviewPanel('msSqlMcp.editorOptions', 'SQL Editor Options', vscode.ViewColumn.Active,
      { enableScripts: true, retainContextWhenHidden: true });
    panel = opened;
    opened.onDidDispose(() => { panel = undefined; });
    opened.webview.onDidReceiveMessage(async raw => {
      const msg = parseOptionsMessage(raw);
      if (!msg) {
        log.warn('editorOptions', 'Ignored a malformed message from the form.');
        return;
      }
      if (msg.type === 'cancel') { opened.dispose(); return; }
      if (msg.type === 'openSettingsJson') {
        await vscode.commands.executeCommand('workbench.action.openSettingsJson');
        return;
      }
      const target = msg.scope === 'workspace' ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global;
      try {
        const defaults = config().inspect(OPTION_SETTINGS.shortcuts)?.defaultValue;
        for (const [key, value] of settingsUpdates(msg.values, defaults)) await config().update(key, value, target);
        log.info('editorOptions', `Saved the SQL editor options to the ${msg.scope} settings.`);
        await opened.webview.postMessage({ type: 'saved', ok: true, text: `Saved to the ${msg.scope} settings.` });
      } catch (err) {
        log.error('editorOptions', 'Saving the SQL editor options failed', err);
        await opened.webview.postMessage({ type: 'saved', ok: false, text: err instanceof Error ? err.message : String(err) });
      }
    });
    render(opened);
  }));
}
