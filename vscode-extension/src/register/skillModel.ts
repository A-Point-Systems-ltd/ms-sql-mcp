// The bundled agent skill (mssql-insights-ops): where it is installed and when a copy may be written.
// No 'vscode' import (unit-testable); skillInstall.ts does the I/O.
import { createHash } from 'crypto';
import * as path from 'path';

export const SKILL_NAME = 'mssql-insights-ops';
/** The skill inside the extension package (staged by scripts/stage-skill.mjs at packaging time). */
export const BUNDLED_SKILL_PATH = ['skills', SKILL_NAME, 'SKILL.md'] as const;

export const hashText = (text: string): string => createHash('sha256').update(text.replace(/\r\n/g, '\n')).digest('hex');

/**
 * User-level skill folders to install into: Cursor's (~/.cursor/skills) when running in Cursor or when ~/.cursor
 * exists, and Claude Code's (~/.claude/skills) when ~/.claude exists. Never creates a tool's home folder.
 */
export function skillTargets(home: string, o: { isCursor: boolean; exists: (p: string) => boolean }): string[] {
  const out: string[] = [];
  if (o.isCursor || o.exists(path.join(home, '.cursor'))) out.push(path.join(home, '.cursor', 'skills', SKILL_NAME, 'SKILL.md'));
  if (o.exists(path.join(home, '.claude'))) out.push(path.join(home, '.claude', 'skills', SKILL_NAME, 'SKILL.md'));
  return out;
}

/**
 * What to do with one installed copy:
 * - 'write': none there, or it is the copy this extension wrote last time (unchanged since) and the bundle differs;
 * - 'current': it already equals the bundled skill;
 * - 'userEdited': it differs from what this extension last wrote (or was never written by it): left alone.
 */
export function skillAction(installed: string | undefined, lastWrittenHash: string | undefined, bundled: string): 'write' | 'current' | 'userEdited' {
  if (installed === undefined) return 'write';
  const current = hashText(installed);
  if (current === hashText(bundled)) return 'current';
  return lastWrittenHash !== undefined && current === lastWrittenHash ? 'write' : 'userEdited';
}
