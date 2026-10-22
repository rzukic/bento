// The table names a template makes: its own names, with a prefix when one is taken.

export type TemplateNames = Record<string, string>;

export function templateNames(base: TemplateNames, prefix: string): TemplateNames {
  const clean = prefix
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_]/g, "_");
  return Object.fromEntries(Object.entries(base).map(([key, name]) => [key, `${clean}${name}`]));
}

/** The names that exist already. */
export function templateConflicts(names: TemplateNames, existing: { name: string }[]): string[] {
  const taken = new Set(existing.map((t) => t.name.toLowerCase()));
  return Object.values(names).filter((name) => taken.has(name.toLowerCase()));
}

/** A prefix that avoids every existing table: none if the plain names are free. */
export function suggestTemplatePrefix(base: TemplateNames, prefixes: string[], existing: { name: string }[]): string {
  for (const prefix of ["", ...prefixes]) if (templateConflicts(templateNames(base, prefix), existing).length === 0) return prefix;
  for (let i = 2; ; i++) {
    const prefix = `${prefixes[0] ?? "t_"}${i}_`;
    if (templateConflicts(templateNames(base, prefix), existing).length === 0) return prefix;
  }
}

/** Relation names that are free in the model: "a_b", else "a_b_2" … */
export function uniqueNamer(existing: { name: string }[]): (name: string) => string {
  const taken = new Set(existing.map((r) => r.name.toLowerCase()));
  return (name) => {
    let candidate = name;
    for (let i = 2; taken.has(candidate.toLowerCase()); i++) candidate = `${name}_${i}`;
    taken.add(candidate.toLowerCase());
    return candidate;
  };
}

/** A date `back` days before today, as YYYY-MM-DD (UTC, like the example rows of every template). */
export function daysAgo(today: Date, back: number): string {
  return new Date(Date.UTC(today.getFullYear(), today.getMonth(), today.getDate() - back)).toISOString().slice(0, 10);
}
