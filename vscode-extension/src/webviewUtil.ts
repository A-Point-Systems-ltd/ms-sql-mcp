// Small helpers shared by the webviews. No 'vscode' import (unit-testable).

import { randomBytes } from 'crypto';

export function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/** A fresh CSP script nonce (128 random bits, hex). */
export function makeNonce(): string {
  return randomBytes(16).toString('hex');
}
