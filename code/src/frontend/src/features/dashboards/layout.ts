/**
 * The dashboard grid: 12 columns, rows of fixed height. Items never
 * overlap; moving or resizing one pushes the ones it lands on downwards,
 * then everything floats up into free space (like a newspaper column).
 */
export const GRID_COLUMNS = 12;

export type LayoutItem = { id: string; x: number; y: number; w: number; h: number };

export function collides(a: LayoutItem, b: LayoutItem): boolean {
  return a.id !== b.id && a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
}

function clamp(item: LayoutItem): LayoutItem {
  const w = Math.max(1, Math.min(GRID_COLUMNS, Math.round(item.w)));
  const h = Math.max(1, Math.min(24, Math.round(item.h)));
  const x = Math.max(0, Math.min(GRID_COLUMNS - w, Math.round(item.x)));
  const y = Math.max(0, Math.round(item.y));
  return { ...item, x, y, w, h };
}

function byPosition(a: LayoutItem, b: LayoutItem): number {
  return a.y - b.y || a.x - b.x;
}

/** Floats every item up as far as it goes, top items first. */
export function compact(items: LayoutItem[], fixedId?: string): LayoutItem[] {
  const placed: LayoutItem[] = [];
  const ordered = [...items].sort(byPosition);
  // The item being dragged keeps its row so it does not jump while moving.
  const fixed = fixedId ? ordered.find((i) => i.id === fixedId) : undefined;
  if (fixed) placed.push(fixed);
  for (const item of ordered) {
    if (item === fixed) continue;
    let candidate = { ...item, y: 0 };
    while (placed.some((other) => collides(candidate, other))) candidate = { ...candidate, y: candidate.y + 1 };
    placed.push(candidate);
  }
  const order = new Map(items.map((item, index) => [item.id, index]));
  return placed.sort((a, b) => order.get(a.id)! - order.get(b.id)!);
}

/** Puts `id` at a new place or size and pushes whatever it covers down. */
function place(items: LayoutItem[], next: LayoutItem): LayoutItem[] {
  const target = clamp(next);
  const others = items.filter((i) => i.id !== target.id).sort(byPosition);
  const settled: LayoutItem[] = [target];
  for (const item of others) {
    let candidate = item;
    while (settled.some((other) => collides(candidate, other))) candidate = { ...candidate, y: candidate.y + 1 };
    settled.push(candidate);
  }
  const order = new Map(items.map((item, index) => [item.id, index]));
  return settled.sort((a, b) => order.get(a.id)! - order.get(b.id)!);
}

export function moveItem(items: LayoutItem[], id: string, x: number, y: number): LayoutItem[] {
  const item = items.find((i) => i.id === id);
  if (!item) return items;
  return compact(place(items, { ...item, x, y }), id);
}

export function resizeItem(items: LayoutItem[], id: string, w: number, h: number): LayoutItem[] {
  const item = items.find((i) => i.id === id);
  if (!item) return items;
  return compact(place(items, { ...item, w, h }), id);
}

/** A new item goes into the first free spot, scanning rows from the top. */
export function addItem(items: LayoutItem[], id: string, w: number, h: number): LayoutItem[] {
  const width = Math.min(GRID_COLUMNS, w);
  const bottom = items.reduce((max, item) => Math.max(max, item.y + item.h), 0);
  for (let y = 0; y <= bottom; y++) {
    for (let x = 0; x + width <= GRID_COLUMNS; x++) {
      const candidate = { id, x, y, w: width, h };
      if (!items.some((other) => collides(candidate, other))) return [...items, candidate];
    }
  }
  return [...items, { id, x: 0, y: bottom, w: width, h }];
}

export function removeItem(items: LayoutItem[], id: string): LayoutItem[] {
  return compact(items.filter((i) => i.id !== id));
}

export function layoutHeight(items: LayoutItem[]): number {
  return items.reduce((max, item) => Math.max(max, item.y + item.h), 0);
}
