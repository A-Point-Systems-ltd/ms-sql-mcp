// Name filter for the object tree. Pure (no vscode import) so it is unit-testable.

/** Normalise raw input into a filter term; empty means "no filter". */
export function normalizeFilter(raw: string | undefined): string {
  return (raw ?? '').trim();
}

/** SQL `LIKE '%term%'`-style semantics: case-insensitive "name contains term". */
export function matchesFilter(name: string, term: string): boolean {
  if (term.length === 0) {
    return true;
  }
  return name.toLocaleLowerCase().includes(term.toLocaleLowerCase());
}
