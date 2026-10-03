#!/usr/bin/env node
// Copies the agent skill (repo .cursor/skills/mssql-insights-ops/SKILL.md, the single source) into the extension
// (skills/, git-ignored, shipped in the VSIX), where the extension installs it from on activation. Runs in
// vscode:prepublish, so every `vsce package` ships the current skill.
import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = resolve(root, '..', '.cursor', 'skills', 'mssql-insights-ops', 'SKILL.md');
const target = resolve(root, 'skills', 'mssql-insights-ops', 'SKILL.md');
mkdirSync(dirname(target), { recursive: true });
copyFileSync(source, target);
console.log(`stage-skill: ${source} -> ${target}`);
