// HTML of the query results panel. No 'vscode' import (unit-testable).
// Each result set renders with the shared data grid (grid/gridHtml: resize, copy button, local sort); the page script
// also switches tabs, posts link / cancel clicks and ticks the timer.
// Rows can hold client personal data: everything is escaped and nothing is loaded from the network. Rows leave the
// panel only through the user's copy actions and Export CSV (modal confirmation), resolved on the extension side.
import { GRID_CSS, gridScript, renderGrid } from '../grid/gridHtml';
import type { GridViewState } from '../grid/gridModel';
import { escapeHtml } from '../webviewUtil';
import type { RunScriptMessage, RunScriptResult, RunScriptResultSet } from './runScript';

export type ResultsState =
  | { kind: 'empty' }
  | { kind: 'running'; connection: string; startedAt: number }
  | { kind: 'done'; connection: string; result: RunScriptResult; lineOffset: number; cancelled?: false }
  | { kind: 'failed'; connection: string; error: string }
  | { kind: 'cancelled'; connection: string };

export const EMPTY_TEXT = 'Run a query with F5 or the Run button.';

const plural = (n: number, word: string): string => `${n} ${word}${n === 1 ? '' : 's'}`;

/**
 * The results webview document for `state`. `cspSource` is accepted for the view contract but deliberately not
 * added to the CSP: the page loads nothing, everything is inline. `gen` is the render counter the grids carry
 * (`data-gen`) and their messages echo; `views` restores each result set's grid view (column order, hidden, ...).
 */
export function renderResults(state: ResultsState, nonce: string, cspSource: string, gen = 0, views: readonly (GridViewState | undefined)[] = []): string {
  void cspSource;
  return page(nonce, header(state), body(state, gen, views));
}

function header(state: ResultsState): string {
  if (state.kind === 'empty') return '';
  const conn = `<span class="conn" title="Connection">${escapeHtml(state.connection)}</span>`;
  switch (state.kind) {
    case 'running':
      return `${conn}<span class="status running">Running... <span id="elapsed" data-started="${state.startedAt}">0:00</span></span>`
        + '<button id="cancel" type="button" title="Cancel the running query">Cancel</button>';
    case 'done':
      return state.result.hadErrors
        ? `${conn}<span class="status error">Completed with errors</span>`
        : `${conn}<span class="status ok">Completed in ${Math.round(state.result.elapsedMs)} ms</span>`;
    case 'failed':
      return `${conn}<span class="status error">${escapeHtml(state.error)}</span>`;
    case 'cancelled':
      return `${conn}<span class="status warning">Cancelled</span>`;
  }
}

function body(state: ResultsState, gen: number, views: readonly (GridViewState | undefined)[]): string {
  if (state.kind === 'empty') return `<p class="hint">${escapeHtml(EMPTY_TEXT)}</p>`;
  if (state.kind !== 'done') return '';
  const { resultSets, messages } = state.result;
  // Messages first when there is nothing else to show (this covers errors-only runs).
  const selected = resultSets.length === 0 ? 'messages' : 'results';
  const tab = (id: string, label: string) =>
    `<button type="button" class="tab${selected === id ? ' selected' : ''}" data-tab="${id}" role="tab" aria-selected="${selected === id}">${label}</button>`;
  const pane = (id: string, content: string) =>
    `<section class="pane${selected === id ? '' : ' hidden'}" id="pane-${id}" role="tabpanel">${content}</section>`;
  const multi = resultSets.length > 1;
  return `<nav class="tabs" role="tablist">${tab('results', `Results (${resultSets.length})`)}${tab('messages', `Messages (${messages.length})`)}</nav>
<div class="content">
${pane('results', resultSets.length ? resultSets.map((set, i) => grid(set, i, multi, gen, views[i])).join('\n') : '<p class="none">No result sets.</p>')}
${pane('messages', messages.map(message).join('\n'))}
</div>`;
}

/** Result set `index` (0-based) as a shared grid with local sort; `set` in its copy messages is that index. */
function grid(set: RunScriptResultSet, index: number, multi: boolean, gen: number, view: GridViewState | undefined): string {
  let caption = `Result ${index + 1} - ${plural(set.rowCount, 'row')}`;
  if (set.truncated) caption += ` (showing first ${set.rows.length})`;
  const id = `g${index}`;
  return `<div class="set${multi ? ' multi' : ' single'}">
<div class="caption">${escapeHtml(caption)}<span class="sortnote" data-sortnote="${id}" style="display:none"> · sorted locally (loaded rows only)</span><span class="filternote" data-filternote="${id}" style="display:none"> · filtered locally</span></div>
<div class="grid ${multi ? 'multi' : 'single'}">${renderGrid({ id, columns: set.columns, rows: set.rows, sortMode: 'local', set: index, gen, view })}</div>
</div>`;
}

function message(m: RunScriptMessage): string {
  const text = escapeHtml(m.text);
  const content = m.line === null
    ? text
    : `<a href="#" class="line" data-line="${m.line}" title="Go to line ${m.line} in the editor">${text}</a>`;
  return `<div class="msg ${m.kind}">${content}</div>`;
}

function page(nonce: string, headerHtml: string, bodyHtml: string): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
  html, body { height: 100%; }
  body { margin: 0; padding: 0; display: flex; flex-direction: column; overflow: hidden;
         font-family: var(--vscode-font-family); font-size: var(--vscode-font-size);
         color: var(--vscode-foreground); background: var(--vscode-panel-background, var(--vscode-editor-background)); }
  header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; padding: 4px 10px; min-height: 24px;
           border-bottom: 1px solid var(--vscode-panel-border); }
  header:empty { display: none; }
  .conn { font-weight: 600; }
  .status.ok { color: var(--vscode-descriptionForeground); }
  .status.error { color: var(--vscode-errorForeground); white-space: pre-wrap; }
  .status.warning { color: var(--vscode-editorWarning-foreground); }
  #elapsed { font-variant-numeric: tabular-nums; }
  button { font-family: inherit; font-size: inherit; cursor: pointer; }
  #cancel { padding: 2px 10px; border: none; border-radius: 2px;
            color: var(--vscode-button-foreground); background: var(--vscode-button-background); }
  #cancel:hover { background: var(--vscode-button-hoverBackground); }
  .tabs { display: flex; gap: 4px; padding: 0 10px; border-bottom: 1px solid var(--vscode-panel-border); }
  .tab { padding: 4px 8px; border: none; border-bottom: 2px solid transparent; background: transparent;
         color: var(--vscode-panelTitle-inactiveForeground, var(--vscode-descriptionForeground)); }
  .tab.selected { color: var(--vscode-panelTitle-activeForeground, var(--vscode-foreground));
                  border-bottom-color: var(--vscode-panelTitle-activeBorder, var(--vscode-focusBorder)); }
  .content { flex: 1; min-height: 0; display: flex; flex-direction: column; }
  .pane { flex: 1; min-height: 0; overflow: auto; padding: 6px 10px; }
  .pane.hidden { display: none; }
  #pane-results { display: flex; flex-direction: column; gap: 10px; }
  #pane-results.hidden { display: none; }
  .set.single { flex: 1; min-height: 0; display: flex; flex-direction: column; }
  .caption { color: var(--vscode-descriptionForeground); padding: 2px 0 4px; }
  .grid { display: flex; flex-direction: column; overflow: hidden; border: 1px solid var(--vscode-panel-border); }
  .grid.single { flex: 1; min-height: 0; }
  .grid.multi { max-height: 45vh; }
  .grid > .dgrid { flex: 1 1 auto; }
${GRID_CSS}
  .msg { padding: 2px 0; white-space: pre-wrap; font-family: var(--vscode-editor-font-family), monospace;
         font-size: var(--vscode-editor-font-size, 12px); user-select: text; }
  .msg.error, .msg.error a { color: var(--vscode-errorForeground); }
  .msg.warning, .msg.warning a { color: var(--vscode-editorWarning-foreground); }
  .msg a { color: inherit; text-decoration: none; }
  .msg a:hover { text-decoration: underline; }
  .hint { padding: 10px; color: var(--vscode-descriptionForeground); }
  .none { margin: 0; color: var(--vscode-descriptionForeground); }
</style>
</head>
<body>
<header>${headerHtml}</header>
${bodyHtml}
<script nonce="${nonce}">
${gridScript()}
  (function () {
    const vscode = acquireVsCodeApi();
    initGrids(vscode);
    for (const tab of document.querySelectorAll('.tab')) {
      tab.addEventListener('click', () => {
        for (const t of document.querySelectorAll('.tab')) {
          const on = t === tab;
          t.setAttribute('class', on ? 'tab selected' : 'tab');
          t.setAttribute('aria-selected', String(on));
          document.getElementById('pane-' + t.dataset.tab).setAttribute('class', on ? 'pane' : 'pane hidden');
        }
      });
    }
    for (const link of document.querySelectorAll('a[data-line]')) {
      link.addEventListener('click', (e) => {
        e.preventDefault();
        vscode.postMessage({ type: 'reveal', line: Number(link.dataset.line) });
      });
    }
    const cancel = document.getElementById('cancel');
    if (cancel) {
      cancel.addEventListener('click', () => {
        cancel.setAttribute('disabled', '');
        vscode.postMessage({ type: 'cancel' });
      });
    }
    const elapsed = document.getElementById('elapsed');
    if (elapsed) {
      const started = Number(elapsed.dataset.started);
      const tick = () => {
        const s = Math.max(0, Math.floor((Date.now() - started) / 1000));
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        const ss = String(s % 60).padStart(2, '0');
        elapsed.textContent = h > 0 ? h + ':' + String(m).padStart(2, '0') + ':' + ss : m + ':' + ss;
      };
      tick();
      // The document is replaced on every state change, which also ends this timer.
      const timer = setInterval(tick, 1000);
      window.addEventListener('unload', () => clearInterval(timer));
    }
  })();
</script>
</body>
</html>`;
}
