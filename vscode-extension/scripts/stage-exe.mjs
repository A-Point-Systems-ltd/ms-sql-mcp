#!/usr/bin/env node
// Stage a QA'd MssqlMcp.exe into the extension for packaging.
//
// Usage:
//   node scripts/stage-exe.mjs <path-to-exe> [--version X.Y.Z] [--version-txt <path>]
//
// - Copies <path-to-exe> to vscode-extension/bin/MssqlMcp.exe
// - If a VERSION.txt is given (or found next to the exe), verifies the copied
//   file's SHA256 against the SHA256 recorded there (same discipline as
//   Deploy-McpRelease.ps1). Mismatch => non-zero exit.
// - If --version is given, writes it into package.json "version" so the
//   marketplace release mirrors the MCP version.

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync, copyFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const extensionRoot = resolve(__dirname, '..');

function parseArgs(argv) {
  const out = { exe: undefined, version: undefined, versionTxt: undefined, sha256: undefined };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--version') {
      out.version = argv[++i];
    } else if (a === '--version-txt') {
      out.versionTxt = argv[++i];
    } else if (a === '--sha256') {
      out.sha256 = argv[++i];
    } else if (!out.exe) {
      out.exe = a;
    }
  }
  return out;
}

function fail(message) {
  console.error(`stage-exe: ${message}`);
  process.exit(1);
}

function sha256(filePath) {
  return createHash('sha256').update(readFileSync(filePath)).digest('hex').toLowerCase();
}

/** Pull the first 64-hex-char SHA256 token out of a VERSION.txt manifest. */
function expectedSha(versionTxtPath) {
  const text = readFileSync(versionTxtPath, 'utf8');
  const match = text.match(/\b([0-9a-fA-F]{64})\b/);
  return match ? match[1].toLowerCase() : undefined;
}

const { exe, version, versionTxt, sha256: expectedSha256 } = parseArgs(process.argv.slice(2));

if (!exe) {
  fail('missing <path-to-exe>. Usage: node scripts/stage-exe.mjs <exe> [--version X.Y.Z] [--version-txt <path>]');
}
const exePath = resolve(exe);
if (!existsSync(exePath)) {
  fail(`exe not found: ${exePath}`);
}

// Copy into bin/
const binDir = join(extensionRoot, 'bin');
mkdirSync(binDir, { recursive: true });
const destPath = join(binDir, 'MssqlMcp.exe');
copyFileSync(exePath, destPath);
const sizeMb = (statSync(destPath).size / (1024 * 1024)).toFixed(1);
console.log(`stage-exe: copied ${exePath} -> ${destPath} (${sizeMb} MB)`);

// Verify SHA256: an explicit --sha256 wins (release-manifest.json / SHA256SUMS.txt),
// otherwise fall back to scanning a VERSION.txt next to the exe.
if (expectedSha256) {
  const expected = expectedSha256.trim().toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(expected)) {
    fail(`--sha256 '${expectedSha256}' is not a 64-character hex digest.`);
  }
  const actual = sha256(destPath);
  if (actual !== expected) {
    fail(`SHA256 mismatch. expected ${expected}, got ${actual}. The staged exe is not the released binary.`);
  }
  console.log(`stage-exe: SHA256 verified against the release manifest (${expected}).`);
} else {
  const versionTxtPath = versionTxt ? resolve(versionTxt) : join(dirname(exePath), 'VERSION.txt');
  if (existsSync(versionTxtPath)) {
    const expected = expectedSha(versionTxtPath);
    if (!expected) {
      console.warn(`stage-exe: no SHA256 found in ${versionTxtPath} — skipping verification.`);
    } else {
      const actual = sha256(destPath);
      if (actual !== expected) {
        fail(`SHA256 mismatch. expected ${expected}, got ${actual}. The staged exe does not match VERSION.txt.`);
      }
      console.log(`stage-exe: SHA256 verified against VERSION.txt (${expected}).`);
    }
  } else {
    console.warn(`stage-exe: no VERSION.txt at ${versionTxtPath} — skipping SHA256 verification.`);
  }
}

// Sync package.json version to the MCP version.
if (version) {
  const normalized = version.replace(/^v/, '');
  if (!/^\d+\.\d+\.\d+/.test(normalized)) {
    fail(`--version '${version}' is not a valid semver (expected X.Y.Z).`);
  }
  const pkgPath = join(extensionRoot, 'package.json');
  const pkg = JSON.parse(readFileSync(pkgPath, 'utf8'));
  pkg.version = normalized;
  writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n');
  console.log(`stage-exe: set package.json version to ${normalized}.`);
}

console.log('stage-exe: done.');
