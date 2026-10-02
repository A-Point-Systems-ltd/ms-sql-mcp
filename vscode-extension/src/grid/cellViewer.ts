import * as vscode from 'vscode';
import { titlePart } from '../query/sqlDocNames';
import { viewerContent } from './cellFormat';

export const CELL_SCHEME = 'mssql-cell';

/** How long a closed viewer's text is kept, so a language switch (close + reopen of the same uri) still finds it. */
const DROP_DELAY_MS = 5000;

/**
 * Read-only `mssql-cell:` documents showing one grid value in full (pretty JSON, indented XML, or plain text). The
 * text is held in memory only and dropped once its document is closed; nothing is written to disk or logged.
 */
export class CellViewer implements vscode.TextDocumentContentProvider, vscode.Disposable {
  private readonly texts = new Map<string, string>();
  private readonly timers = new Set<NodeJS.Timeout>();
  private seq = 0;
  private readonly subs: vscode.Disposable[];

  constructor() {
    this.subs = [
      vscode.workspace.registerTextDocumentContentProvider(CELL_SCHEME, this),
      vscode.workspace.onDidCloseTextDocument(doc => {
        if (doc.uri.scheme !== CELL_SCHEME) return;
        const key = doc.uri.toString();
        const timer = setTimeout(() => {
          this.timers.delete(timer);
          if (!vscode.workspace.textDocuments.some(d => d.uri.toString() === key)) this.texts.delete(key);
        }, DROP_DELAY_MS);
        this.timers.add(timer);
      }),
    ];
  }

  /** Opens `value` in a new viewer tab titled `title` (`<column> · row <n> - <object>`). */
  async open(title: string, value: unknown): Promise<void> {
    const { text, language } = viewerContent(value);
    const uri = vscode.Uri.from({ scheme: CELL_SCHEME, path: `/${titlePart(title)}`, query: `v=${++this.seq}` });
    this.texts.set(uri.toString(), text);
    const doc = await vscode.workspace.openTextDocument(uri);
    const typed = doc.languageId === language ? doc : await vscode.languages.setTextDocumentLanguage(doc, language);
    await vscode.window.showTextDocument(typed, { preview: true });
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    return this.texts.get(uri.toString()) ?? 'This value is no longer available. Open it again from the grid.';
  }

  dispose(): void {
    for (const t of this.timers) clearTimeout(t);
    this.timers.clear();
    for (const s of this.subs) s.dispose();
    this.texts.clear();
  }
}
