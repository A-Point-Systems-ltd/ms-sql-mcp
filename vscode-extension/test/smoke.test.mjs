import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';

test('compiled extension entry exists', () => {
  assert.ok(existsSync(new URL('../out/extension.js', import.meta.url)));
});
