// In-flight call bookkeeping for the private server processes. No 'vscode' import (unit-testable).
import { McpToolError } from './parse';

/**
 * Counts the calls running on one server process, so a restart can wait for them instead of killing a running
 * script.
 */
export class CallCounter {
  private count = 0;
  private idle: (() => void)[] = [];

  get inFlight(): number {
    return this.count;
  }

  /** Marks a call as started. The returned function ends it; calling it again does nothing. */
  begin(): () => void {
    this.count++;
    let ended = false;
    return () => {
      if (ended) return;
      ended = true;
      this.count--;
      if (this.count === 0) this.flush();
    };
  }

  /** Runs `fn` once no call is in flight: at once when idle, otherwise after the last running call ends. */
  whenIdle(fn: () => void): void {
    this.idle.push(fn);
    if (this.count === 0) this.flush();
  }

  private flush(): void {
    const callbacks = this.idle;
    this.idle = [];
    for (const fn of callbacks) fn();
  }
}

/** The error of a call abandoned through its AbortSignal (same as McpStdioClient's). */
export function cancelledError(): McpToolError {
  return new McpToolError('Cancelled.', { cancelled: true });
}

/**
 * Settles with `promise`, or rejects at once with {@link cancelledError} when `signal` fires first. A later
 * rejection of the losing `promise` is swallowed here (its owner handles it), so it never goes unhandled.
 */
export function raceAbort<T>(promise: Promise<T>, signal: AbortSignal | undefined): Promise<T> {
  if (!signal) return promise;
  if (signal.aborted) {
    promise.catch(() => undefined);
    return Promise.reject(cancelledError());
  }
  return new Promise<T>((resolve, reject) => {
    const onAbort = () => reject(cancelledError());
    signal.addEventListener('abort', onAbort, { once: true });
    promise.then(
      value => { signal.removeEventListener('abort', onAbort); resolve(value); },
      err => { signal.removeEventListener('abort', onAbort); reject(err); },
    );
  });
}

/**
 * Gets the current process from `ensure` (raced against `signal`) and checks it is still current, because a
 * reset can land between `ensure()` resolving and the call being counted. Retries up to `attempts` times.
 */
export async function acquireCurrent<T>(
  ensure: () => Promise<T>, isCurrent: (client: T) => boolean, signal: AbortSignal | undefined, attempts = 3,
): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    const client = await raceAbort(ensure(), signal);
    if (isCurrent(client)) return client;
    if (attempt >= attempts) throw new Error('The server process restarted while the call was starting.');
  }
}
