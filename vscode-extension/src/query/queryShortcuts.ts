// Query shortcuts (Ctrl+0 … Ctrl+9): the `msSqlMcp.queryShortcuts` setting and its `{selection}` template. No `vscode`
// import, so the unit tests load it directly.

/** The setting under `msSqlMcp`: an object mapping a digit ("0".."9") to SQL. */
export const QUERY_SHORTCUTS_SETTING = 'queryShortcuts';

/** Replaced by the selected text, or the name under the cursor when nothing is selected. */
export const SELECTION_PLACEHOLDER = '{selection}';

/** The slots, one per digit key. */
export const SHORTCUT_SLOTS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9] as const;

/** The context key that turns slot `n`'s keybinding on (only configured slots take Ctrl+n away from VS Code). */
export const shortcutContextKey = (slot: number): string => `msSqlMcp.shortcut.${slot}`;

/** The configured slots: digit keys with non-blank SQL. Anything else (a blank slot clears a default) is left out. */
export function parseShortcuts(value: unknown): Map<number, string> {
  const result = new Map<number, string>();
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return result;
  for (const [key, sql] of Object.entries(value)) {
    if (!/^[0-9]$/.test(key) || typeof sql !== 'string' || !sql.trim()) continue;
    result.set(Number(key), sql);
  }
  return result;
}

export type ShortcutExpansion = { sql: string } | { error: string };

/**
 * The SQL a shortcut runs. With `{selection}`: the selected text (trimmed), else `nameAtCursor`; an error when there
 * is neither. Without the placeholder the template runs as written and the selection is ignored.
 */
export function expandShortcut(template: string, selection: string | undefined, nameAtCursor: string | undefined): ShortcutExpansion {
  if (!template.includes(SELECTION_PLACEHOLDER)) return { sql: template };
  const value = selection?.trim() || nameAtCursor?.trim();
  if (!value) return { error: `this shortcut uses ${SELECTION_PLACEHOLDER}: select text or put the cursor on a name first.` };
  return { sql: template.split(SELECTION_PLACEHOLDER).join(value) };
}

/**
 * The settings value to write for the edited slots: non-blank slots as entered, plus an empty string for each slot
 * that has a default but was cleared (VS Code merges object settings with their default, so leaving the key out would
 * bring the default back). Slots equal to their default are still written, so the user's copy is explicit.
 */
export function shortcutsToSetting(slots: ReadonlyMap<number, string>, defaults: unknown): Record<string, string> {
  const result: Record<string, string> = {};
  const defaultSlots = parseShortcuts(defaults);
  for (const slot of SHORTCUT_SLOTS) {
    const sql = slots.get(slot)?.trim() ? slots.get(slot)! : '';
    if (sql) result[String(slot)] = sql;
    else if (defaultSlots.has(slot)) result[String(slot)] = '';
  }
  return result;
}
