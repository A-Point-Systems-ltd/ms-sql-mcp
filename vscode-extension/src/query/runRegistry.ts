// One run per document, keyed by document uri. No 'vscode' import (unit-testable).
//
// Each run gets its own token. Closing a document aborts its run and forgets the token at once, so a new document
// that reuses the same uri (untitled) never inherits the old run's guard, Cancel or results: the old run checks
// isCurrent(token) before every update and finds it is no longer current.

export interface RunToken {
  readonly key: string;
  readonly controller: AbortController;
}

export class RunRegistry {
  private readonly runs = new Map<string, RunToken>();

  /** True while a run of document `key` is current. */
  has(key: string): boolean {
    return this.runs.has(key);
  }

  /** Starts a run of `key`, or returns undefined when one is already running. */
  start(key: string): RunToken | undefined {
    if (this.runs.has(key)) return undefined;
    const token: RunToken = { key, controller: new AbortController() };
    this.runs.set(key, token);
    return token;
  }

  /** Whether `token` is still the run of its document (not finished, not closed, not superseded). */
  isCurrent(token: RunToken): boolean {
    return this.runs.get(token.key) === token;
  }

  /** Ends `token`'s run; a no-op when it is no longer current. */
  finish(token: RunToken): void {
    if (this.isCurrent(token)) this.runs.delete(token.key);
  }

  /** Aborts the current run of `key` (Cancel); it stays current until it settles. */
  cancel(key: string): void {
    this.runs.get(key)?.controller.abort();
  }

  /** The document closed: aborts its run and forgets it at once. Returns whether a run was in flight. */
  close(key: string): boolean {
    const token = this.runs.get(key);
    if (!token) return false;
    this.runs.delete(key);
    token.controller.abort();
    return true;
  }

  /** Aborts and forgets every run (extension shutdown). */
  dispose(): void {
    for (const token of this.runs.values()) token.controller.abort();
    this.runs.clear();
  }
}
