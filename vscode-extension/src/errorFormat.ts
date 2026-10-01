// Error rendering for the operational log. Pure (no vscode import) so it is unit-testable.

/**
 * Lines describing `err` for the log, unindented. A V8 stack already opens with
 * `Name: message`, so the header is written separately only when the stack lacks it
 * (or there is no stack) — otherwise every error would print its message twice.
 */
export function formatError(err: unknown): string[] {
  if (err instanceof Error) {
    const header = err.message ? `${err.name}: ${err.message}` : err.name;
    if (!err.stack) {
      return header.split('\n');
    }
    const stack = err.stack.split('\n');
    return err.stack.startsWith(header) ? stack : [...header.split('\n'), ...stack];
  }
  return err === undefined ? [] : [String(err)];
}

/** The message of `err` for user-facing text: an Error's message, else its string form. */
export function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
