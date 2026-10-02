// What the cell viewer shows for one value: pretty JSON, indented XML, or the plain text. No 'vscode' import
// (unit-testable). Only whitespace is changed; the characters of the value are kept exactly (JSON numbers are not
// re-parsed, so a big integer keeps all its digits).
import { cellText } from './gridModel';

export type ViewerLanguage = 'json' | 'xml' | 'plaintext';

/**
 * JSON object / array text re-indented with 2 spaces, or undefined when `text` is not a JSON object or array.
 * JSON.parse validates; the re-indent walks the original text, so numbers and string escapes stay as written.
 */
export function prettyJson(text: string): string | undefined {
  const t = text.trim();
  if (!(t.startsWith('{') || t.startsWith('['))) return undefined;
  try {
    const v: unknown = JSON.parse(t);
    if (typeof v !== 'object' || v === null) return undefined;
  } catch {
    return undefined;
  }
  let out = '';
  let depth = 0;
  let inString = false;
  const nl = () => `\n${'  '.repeat(depth)}`;
  for (let i = 0; i < t.length; i++) {
    const ch = t[i];
    if (inString) {
      out += ch;
      if (ch === '\\') { out += t[++i]; continue; }
      if (ch === '"') inString = false;
      continue;
    }
    switch (ch) {
      case '"': inString = true; out += ch; break;
      case '{':
      case '[': {
        // An empty {} / [] stays on one line.
        let j = i + 1;
        while (j < t.length && /\s/.test(t[j])) j++;
        if (t[j] === (ch === '{' ? '}' : ']')) { out += ch + t[j]; i = j; break; }
        depth++;
        out += ch + nl();
        break;
      }
      case '}':
      case ']': depth--; out += nl() + ch; break;
      case ',': out += ',' + nl(); break;
      case ':': out += ': '; break;
      default:
        if (!/\s/.test(ch)) out += ch;
    }
  }
  return out;
}

/**
 * XML text indented with 2 spaces per level, or undefined when it does not start with '<' or is not well-formed
 * enough for a simple tokenizer (unbalanced or mismatched tags). Comments, CDATA, processing instructions and a
 * DOCTYPE each go on their own line; an element holding only text stays on one line.
 */
export function prettyXml(text: string): string | undefined {
  const t = text.trim();
  if (!t.startsWith('<')) return undefined;
  const tokens = t.match(/<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>|<!DOCTYPE[^>]*>|<\/?[^<>]+>|[^<]+/gi);
  if (!tokens || tokens.join('').length !== t.length) return undefined;
  const lines: string[] = [];
  const stack: string[] = [];
  const pad = () => '  '.repeat(stack.length);
  const nameOf = (tag: string) => /^<\/?\s*([^\s/>]+)/.exec(tag)?.[1];
  for (let i = 0; i < tokens.length; i++) {
    const tok = tokens[i];
    if (/^<(!--|!\[CDATA\[|\?|!DOCTYPE)/i.test(tok)) { lines.push(pad() + tok); continue; }
    if (tok.startsWith('</')) {
      const name = nameOf(tok);
      if (!name || stack.pop() !== name) return undefined;
      lines.push(pad() + tok);
      continue;
    }
    if (tok.startsWith('<')) {
      const name = nameOf(tok);
      if (!name) return undefined;
      if (/\/\s*>$/.test(tok)) { lines.push(pad() + tok); continue; }
      // <a>text</a> stays on one line.
      const next = tokens[i + 1];
      const after = tokens[i + 2];
      if (next !== undefined && !next.startsWith('<') && after !== undefined && after.startsWith('</') && nameOf(after) === name) {
        lines.push(pad() + tok + next.trim() + after);
        i += 2;
        continue;
      }
      lines.push(pad() + tok);
      stack.push(name);
      continue;
    }
    const textPart = tok.trim();
    if (textPart) lines.push(pad() + textPart);
  }
  return stack.length === 0 ? lines.join('\n') : undefined;
}

/** The viewer document for a cell value: JSON / XML when they parse, otherwise the display text (binary as 0x…). */
export function viewerContent(value: unknown): { text: string; language: ViewerLanguage } {
  if (value === null || value === undefined) return { text: 'NULL', language: 'plaintext' };
  if (typeof value === 'object') return { text: JSON.stringify(value, null, 2), language: 'json' };
  const text = cellText(value);
  if (typeof value === 'string') {
    const json = prettyJson(text);
    if (json !== undefined) return { text: json, language: 'json' };
    const xml = prettyXml(text);
    if (xml !== undefined) return { text: xml, language: 'xml' };
  }
  return { text, language: 'plaintext' };
}

/** The viewer tab title: `<column> · row <n> - <object>` (n is 1-based). */
export function viewerTitle(column: string, row: number, object: string): string {
  return `${column || '(No column name)'} · row ${row + 1} - ${object}`;
}
