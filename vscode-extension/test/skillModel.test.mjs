import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { SKILL_NAME, hashText, skillAction, skillTargets } from '../out/register/skillModel.js';

test('skill targets: Cursor when running in it (or ~/.cursor exists), Claude Code only when ~/.claude exists', () => {
  const home = path.join('C:', 'Users', 'u');
  const cursor = path.join(home, '.cursor', 'skills', SKILL_NAME, 'SKILL.md');
  const claude = path.join(home, '.claude', 'skills', SKILL_NAME, 'SKILL.md');
  assert.deepEqual(skillTargets(home, { isCursor: true, exists: () => false }), [cursor]);
  assert.deepEqual(skillTargets(home, { isCursor: false, exists: () => false }), []);
  assert.deepEqual(skillTargets(home, { isCursor: false, exists: p => p.endsWith('.claude') }), [claude]);
  assert.deepEqual(skillTargets(home, { isCursor: true, exists: () => true }), [cursor, claude]);
});

test('skill action: write when missing or still our last copy; never over a user edit', () => {
  const v1 = '---\nname: x\n---\nv1';
  const v2 = '---\nname: x\n---\nv2';
  assert.equal(skillAction(undefined, undefined, v2), 'write');
  assert.equal(skillAction(v2, undefined, v2), 'current');
  assert.equal(skillAction(v2.replace(/\n/g, '\r\n'), undefined, v2), 'current', 'line endings do not count');
  assert.equal(skillAction(v1, hashText(v1), v2), 'write', 'our older copy is updated');
  assert.equal(skillAction(v1 + ' edited', hashText(v1), v2), 'userEdited');
  assert.equal(skillAction(v1, undefined, v2), 'userEdited', 'a copy we never wrote');
});
