// The SQL Editor Options form's values, their settings and the messages from the form. No 'vscode' import
// (unit-testable).

import { SHORTCUT_SLOTS, parseShortcuts, shortcutsToSetting } from '../query/queryShortcuts';
import { JoinNameMatch, enhancedEnabled, enhancedOptions } from '../query/sqlEditorFeatures';

export type KeywordCase = 'lower' | 'upper' | 'preserve';
export type SettingsScope = 'user' | 'workspace';

export interface EditorOptionValues {
  enhanced: boolean;
  tableAliases: boolean;
  joinNameMatch: JoinNameMatch;
  keywordCase: KeywordCase;
  maxItemsPerRow: number;
  formatFiles: boolean;
  /** SQL per slot 0..9 ('' = no shortcut). */
  shortcuts: string[];
}

/** The `msSqlMcp.*` setting behind each field (the shortcuts are one object setting). */
export const OPTION_SETTINGS = {
  enhanced: 'completion.enhanced',
  tableAliases: 'completion.tableAliases',
  joinNameMatch: 'completion.joinNameMatch',
  keywordCase: 'format.keywordCase',
  maxItemsPerRow: 'format.maxItemsPerRow',
  formatFiles: 'format.formatFiles',
  shortcuts: 'queryShortcuts',
} as const satisfies Record<keyof EditorOptionValues, string>;

export const KEYWORD_CASES: readonly KeywordCase[] = ['lower', 'upper', 'preserve'];
export const NAME_MATCH_OPTIONS: readonly { value: JoinNameMatch; label: string }[] = [
  { value: 'off', label: 'Off: foreign keys only' },
  { value: 'keyed', label: 'Keyed: one side is its table\'s single-column primary key' },
  { value: 'any', label: 'Any: also tables without a key and non-key columns (listed last)' },
];
export const MAX_ITEMS_PER_ROW = { min: 1, max: 50, fallback: 4 } as const;
/** Longest SQL a shortcut may hold (the form refuses more). */
export const MAX_SHORTCUT_LENGTH = 4000;

/** The form's values from the effective settings (`get` reads `msSqlMcp.<key>`). */
export function valuesFromSettings(get: (key: string) => unknown): EditorOptionValues {
  const { tableAliases, joinNameMatch } = enhancedOptions(get(OPTION_SETTINGS.tableAliases), get(OPTION_SETTINGS.joinNameMatch));
  const keywordCase = get(OPTION_SETTINGS.keywordCase);
  const max = get(OPTION_SETTINGS.maxItemsPerRow);
  const slots = parseShortcuts(get(OPTION_SETTINGS.shortcuts));
  return {
    enhanced: enhancedEnabled(get(OPTION_SETTINGS.enhanced)),
    tableAliases,
    joinNameMatch,
    keywordCase: KEYWORD_CASES.includes(keywordCase as KeywordCase) ? keywordCase as KeywordCase : 'lower',
    maxItemsPerRow: typeof max === 'number' && Number.isInteger(max) && max >= MAX_ITEMS_PER_ROW.min && max <= MAX_ITEMS_PER_ROW.max
      ? max : MAX_ITEMS_PER_ROW.fallback,
    formatFiles: get(OPTION_SETTINGS.formatFiles) === true,
    shortcuts: SHORTCUT_SLOTS.map(slot => slots.get(slot) ?? ''),
  };
}

/** The settings to write: `[key under msSqlMcp, value]`. `shortcutDefaults` is the shortcuts setting's default. */
export function settingsUpdates(values: EditorOptionValues, shortcutDefaults: unknown): [string, unknown][] {
  const slots = new Map(values.shortcuts.map((sql, slot) => [slot, sql] as const));
  return [
    [OPTION_SETTINGS.enhanced, values.enhanced],
    [OPTION_SETTINGS.tableAliases, values.tableAliases],
    [OPTION_SETTINGS.joinNameMatch, values.joinNameMatch],
    [OPTION_SETTINGS.keywordCase, values.keywordCase],
    [OPTION_SETTINGS.maxItemsPerRow, values.maxItemsPerRow],
    [OPTION_SETTINGS.formatFiles, values.formatFiles],
    [OPTION_SETTINGS.shortcuts, shortcutsToSetting(slots, shortcutDefaults)],
  ];
}

export type OptionsMessage =
  | { type: 'save'; values: EditorOptionValues; scope: SettingsScope }
  | { type: 'cancel' }
  | { type: 'openSettingsJson' };

/** A validated message from the form, or undefined (malformed or out of range). */
export function parseOptionsMessage(raw: unknown): OptionsMessage | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const m = raw as Record<string, unknown>;
  if (m.type === 'cancel') return { type: 'cancel' };
  if (m.type === 'openSettingsJson') return { type: 'openSettingsJson' };
  if (m.type !== 'save') return undefined;
  if (m.scope !== 'user' && m.scope !== 'workspace') return undefined;
  const v = m.values as Record<string, unknown> | null | undefined;
  if (!v || typeof v !== 'object') return undefined;
  if (![v.enhanced, v.tableAliases, v.formatFiles].every(b => typeof b === 'boolean')) return undefined;
  if (!NAME_MATCH_OPTIONS.some(o => o.value === v.joinNameMatch)) return undefined;
  if (!KEYWORD_CASES.includes(v.keywordCase as KeywordCase)) return undefined;
  const max = v.maxItemsPerRow;
  if (typeof max !== 'number' || !Number.isInteger(max) || max < MAX_ITEMS_PER_ROW.min || max > MAX_ITEMS_PER_ROW.max) return undefined;
  const shortcuts = v.shortcuts;
  if (!Array.isArray(shortcuts) || shortcuts.length !== SHORTCUT_SLOTS.length
    || !shortcuts.every(s => typeof s === 'string' && s.length <= MAX_SHORTCUT_LENGTH)) return undefined;
  return {
    type: 'save',
    scope: m.scope,
    values: {
      enhanced: v.enhanced as boolean,
      tableAliases: v.tableAliases as boolean,
      joinNameMatch: v.joinNameMatch as JoinNameMatch,
      keywordCase: v.keywordCase as KeywordCase,
      maxItemsPerRow: max,
      formatFiles: v.formatFiles as boolean,
      shortcuts: shortcuts as string[],
    },
  };
}
