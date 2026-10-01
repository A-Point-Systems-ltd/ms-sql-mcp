import * as vscode from 'vscode';
import { makeNonce } from '../webviewUtil';

export const FILTER_VIEW_ID = 'msSqlMcp.objectFilter';

/**
 * The one-line input above the object tree. Tree views cannot host an input box,
 * so this is a minimal webview view that forwards what is typed to `onChange`.
 */
export class ObjectFilterViewProvider implements vscode.WebviewViewProvider {
  private view: vscode.WebviewView | undefined;

  constructor(
    private readonly getTerm: () => string,
    private readonly onChange: (term: string) => void,
  ) {}

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    view.webview.options = { enableScripts: true };
    view.webview.html = renderHtml(this.getTerm());
    view.webview.onDidReceiveMessage((message: { type?: string; value?: unknown }) => {
      if (message?.type === 'filter') {
        this.onChange(typeof message.value === 'string' ? message.value : '');
      }
    });
    view.onDidDispose(() => {
      this.view = undefined;
    });
  }

  /** Push a term set from outside the input (e.g. the Clear Filter command) back into it. */
  sync(term: string): void {
    void this.view?.webview.postMessage({ type: 'set', value: term });
  }

  focus(): void {
    void this.view?.webview.postMessage({ type: 'focus' });
  }
}

function renderHtml(initial: string): string {
  const nonce = makeNonce();
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
  body { padding: 4px 8px; margin: 0; }
  .box { display: flex; align-items: center; gap: 2px;
         background: var(--vscode-input-background);
         border: 1px solid var(--vscode-input-border, transparent); border-radius: 2px; }
  .box:focus-within { border-color: var(--vscode-focusBorder); }
  input { flex: 1; min-width: 0; padding: 4px 6px; border: none; outline: none; background: transparent;
          color: var(--vscode-input-foreground); font-family: var(--vscode-font-family);
          font-size: var(--vscode-font-size); }
  input::placeholder { color: var(--vscode-input-placeholderForeground); }
  button { border: none; background: transparent; color: var(--vscode-icon-foreground);
           cursor: pointer; padding: 0 6px; font-size: 14px; line-height: 1; visibility: hidden; }
  button.shown { visibility: visible; }
  button:hover { color: var(--vscode-foreground); }
</style>
</head>
<body>
<div class="box">
  <input id="q" type="text" placeholder="Filter objects by name" aria-label="Filter objects by name"
         spellcheck="false" dir="auto">
  <button id="clear" title="Clear filter (Esc)" aria-label="Clear filter">&#x2715;</button>
</div>
<script nonce="${nonce}">
  const vscode = acquireVsCodeApi();
  const q = document.getElementById('q');
  const clear = document.getElementById('clear');
  q.value = ${JSON.stringify(initial)};
  let timer;
  const sync = () => clear.classList.toggle('shown', q.value.length > 0);
  const send = () => vscode.postMessage({ type: 'filter', value: q.value });
  sync();
  q.addEventListener('input', () => {
    sync();
    clearTimeout(timer);
    timer = setTimeout(send, 200);
  });
  q.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { clearTimeout(timer); send(); }
    if (e.key === 'Escape') { q.value = ''; sync(); clearTimeout(timer); send(); }
  });
  clear.addEventListener('click', () => { q.value = ''; sync(); send(); q.focus(); });
  window.addEventListener('message', (e) => {
    const m = e.data;
    if (m && m.type === 'set') { q.value = m.value; sync(); }
    if (m && m.type === 'focus') { q.focus(); q.select(); }
  });
</script>
</body>
</html>`;
}
