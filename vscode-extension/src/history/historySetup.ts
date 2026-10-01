import * as vscode from 'vscode';
import type { ServerProcessClient } from '../client/serverProcessClient';
import { pick } from '../client/parse';
import type { ConnectionProfile } from '../connections/profile';
import { errorMessage } from '../errorFormat';
import { Logger } from '../logger';
import {
  CREATE_BUTTON, HISTORY_TOOL, NOT_SET_UP_MESSAGE, OPEN_FIRST_MESSAGE, disabledWarning, incompatibleWarning, installDecision,
  installPrompt, parseHistoryStatus, readOnlyWarning, setUpMessage, targetText,
} from './historyModel';

/**
 * The DDL history set-up after a connection form save with `ddlHistory` turned on. It reads `ddl_history status` on
 * the runner (which applies the pending profile-set reset first, so the saved profile is served), then: nothing when
 * all is in place; a warning for an incompatible existing table, a disabled trigger (left unchanged) or a read-only
 * connection; otherwise a modal that names server/db, the connection and what will be created, and `install` only on
 * Create. Never throws.
 */
export async function setUpDdlHistory(runner: ServerProcessClient, profile: ConnectionProfile, log: Logger): Promise<void> {
  try {
    if (!profile.open) {
      void vscode.window.showInformationMessage(OPEN_FIRST_MESSAGE);
      return;
    }
    const where = targetText(profile);
    let status;
    try {
      status = parseHistoryStatus(await runner.call(profile.name, HISTORY_TOOL, { action: 'status' }));
    } catch (err) {
      log.warn('history', `ddl_history status on '${profile.name}' failed: ${errorMessage(err)}`);
      void vscode.window.showWarningMessage(`DDL history on ${where}: ${errorMessage(err)}`);
      return;
    }
    switch (installDecision(status, profile.readOnly)) {
      case 'none':
        return;
      case 'warnDisabled':
        void vscode.window.showWarningMessage(disabledWarning(where));
        return;
      case 'warnReadOnly':
        void vscode.window.showWarningMessage(readOnlyWarning(where));
        return;
      case 'warnIncompatible':
        void vscode.window.showWarningMessage(incompatibleWarning(where));
        return;
      case 'confirmInstall':
        break;
    }
    const prompt = installPrompt(status, where, profile.name);
    const choice = await vscode.window.showWarningMessage(prompt.message, { modal: true, detail: prompt.detail }, CREATE_BUTTON);
    if (choice !== CREATE_BUTTON) {
      void vscode.window.showInformationMessage(NOT_SET_UP_MESSAGE);
      return;
    }
    try {
      const data = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Creating DDL history on ${where}…` },
        () => runner.call(profile.name, HISTORY_TOOL, { action: 'install' }));
      log.info('history', `ddl_history install on '${profile.name}': createdTable=${pick(data, 'createdTable') === true}, `
        + `createdTrigger=${pick(data, 'createdTrigger') === true}, triggerEnabled=${pick(data, 'triggerEnabled') === true}`);
      void vscode.window.showInformationMessage(setUpMessage(where));
      const warning = pick(data, 'warning');
      if (typeof warning === 'string' && warning) void vscode.window.showWarningMessage(`DDL history on ${where}: ${warning}`);
    } catch (err) {
      log.warn('history', `ddl_history install on '${profile.name}' failed: ${errorMessage(err)}`);
      void vscode.window.showWarningMessage(`DDL history was not set up on ${where}: ${errorMessage(err)}`);
    }
  } catch (err) {
    log.error('history', 'DDL history set-up failed', err);
  }
}
