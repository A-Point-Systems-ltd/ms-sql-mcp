import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { PROVIDER_ID } from '../out/constants.js';

test('PROVIDER_ID matches the contributed mcpServerDefinitionProviders id', () => {
  const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  const ids = pkg.contributes.mcpServerDefinitionProviders.map(p => p.id);
  assert.deepEqual(ids, [PROVIDER_ID]);
});
