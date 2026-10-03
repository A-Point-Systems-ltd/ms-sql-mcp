import * as fs from 'fs/promises';
import { existsSync } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { Logger } from '../logger';
import { BUNDLED_SKILL_PATH, SKILL_NAME, hashText, skillAction, skillTargets } from './skillModel';

/** globalState: installed skill path -> hash of the content this extension last wrote there. */
const WRITTEN_KEY = 'msSqlMcp.skillWritten';

/**
 * Installs (or updates) the bundled mssql-insights-ops agent skill into the user's Cursor / Claude Code skill folders,
 * unless `msSqlMcp.installSkill` is off. A copy the user edited is never overwritten. Best-effort: failures are logged.
 */
export async function installBundledSkill(context: vscode.ExtensionContext, log: Logger): Promise<void> {
  if (!vscode.workspace.getConfiguration('msSqlMcp').get<boolean>('installSkill', true)) return;
  let bundled: string;
  try {
    bundled = await fs.readFile(vscode.Uri.joinPath(context.extensionUri, ...BUNDLED_SKILL_PATH).fsPath, 'utf8');
  } catch {
    log.debug('skill', 'No bundled skill in this build (development run); nothing installed.');
    return;
  }
  const written = { ...context.globalState.get<Record<string, string>>(WRITTEN_KEY, {}) };
  const isCursor = /cursor/i.test(vscode.env.appName);
  for (const target of skillTargets(os.homedir(), { isCursor, exists: existsSync })) {
    try {
      const installed = await fs.readFile(target, 'utf8').catch(() => undefined);
      const action = skillAction(installed, written[target], bundled);
      if (action === 'userEdited') {
        log.info('skill', `${target} was changed outside the extension; the bundled ${SKILL_NAME} skill was not installed over it.`);
        continue;
      }
      if (action === 'write') {
        await fs.mkdir(path.dirname(target), { recursive: true });
        await fs.writeFile(target, bundled, 'utf8');
        log.info('skill', `${installed === undefined ? 'Installed' : 'Updated'} the ${SKILL_NAME} skill: ${target}`);
      }
      written[target] = hashText(bundled);
    } catch (err) {
      log.warn('skill', `Installing the ${SKILL_NAME} skill to ${target} failed: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
  await context.globalState.update(WRITTEN_KEY, written);
}
