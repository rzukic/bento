import { describe, expect, it } from "vitest";
import {
  buildErdRelationEdges,
  type ErdTableNode,
} from "../features/schema-workbench/visual/erdRouting";
import type { SchemaModelDocument, SchemaTable } from "../types/api";

type Point = { x: number; y: number };

function node(name: string, x: number, y: number, width = 280, height = 240): ErdTableNode {
  const table = { name, columns: [], indexes: [] } as unknown as SchemaTable;
  return { table, x, y, width, height };
}

function document(relations: Array<{ name: string; from: string; to: string }>): SchemaModelDocument {
  return {
    version: 1,
    tables: [],
    relations: relations.map((relation) => ({
      name: relation.name,
      type: "1:N",
      from: relation.from,
      to: relation.to,
      column: `${relation.from}_id`,
    })),
  } as unknown as SchemaModelDocument;
}

function parsePath(path: string): Point[] {
  return [...path.matchAll(/[ML] (-?[\d.]+) (-?[\d.]+)/g)].map((match) => ({
    x: Number(match[1]),
    y: Number(match[2]),
  }));
}

/** True when a point lies strictly inside the card (not on its border). */
function isInside(point: Point, card: ErdTableNode): boolean {
  return (
    point.x > card.x &&
    point.x < card.x + card.width &&
    point.y > card.y &&
    point.y < card.y + card.height
  );
}

/** Any horizontal segment that passes through the card interior. */
function crossesCard(points: Point[], card: ErdTableNode): boolean {
  for (let i = 0; i < points.length - 1; i += 1) {
    const a = points[i]!;
    const b = points[i + 1]!;
    if (a.y !== b.y) continue;
    const y = a.y;
    if (y <= card.y || y >= card.y + card.height) continue;
    const minX = Math.min(a.x, b.x);
    const maxX = Math.max(a.x, b.x);
    // Crossing means the segment covers interior x on both sides of a border,
    // i.e. it spans more than touching the edge it ends on.
    if (minX < card.x && maxX > card.x + card.width) return true;
    if (minX < card.x && maxX > card.x + 1) return true;
    if (maxX > card.x + card.width && minX < card.x + card.width - 1) return true;
  }
  return false;
}

describe("ERD relation routing", () => {
  it("routes side-by-side tables across the gap between them", () => {
    const customers = node("customers", 0, 0);
    const orders = node("orders", 340, 0);
    const [edge] = buildErdRelationEdges(
      document([{ name: "customer_orders", from: "customers", to: "orders" }]),
      [customers, orders],
    );

    const points = parsePath(edge!.path);
    expect(points[0]).toEqual(expect.objectContaining({ x: 280 }));
    expect(points.at(-1)).toEqual(expect.objectContaining({ x: 340 }));
    for (const point of points) {
      expect(point.x).toBeGreaterThanOrEqual(280);
      expect(point.x).toBeLessThanOrEqual(340);
    }
  });

  it("does not run across a stacked target table (regression: line behind invoices card)", () => {
    // customers above invoices, orders to the right of customers. Entering
    // invoices from the left while the channel sits right of both tables made
    // the last segment cross the whole invoices card.
    const customers = node("customers", 56, 0, 288, 268);
    const orders = node("orders", 400, 0, 288, 268);
    const invoices = node("invoices", 84, 300, 288, 200);
    const edges = buildErdRelationEdges(
      document([
        { name: "customer_orders", from: "customers", to: "orders" },
        { name: "customer_invoices", from: "customers", to: "invoices" },
      ]),
      [customers, orders, invoices],
    );

    const invoiceEdge = edges.find((edge) => edge.toTable === "invoices");
    expect(invoiceEdge).toBeDefined();
    const points = parsePath(invoiceEdge!.path);

    const end = points.at(-1)!;
    expect([invoices.x, invoices.x + invoices.width]).toContain(end.x);
    expect(crossesCard(points, invoices)).toBe(false);
    expect(crossesCard(points, customers)).toBe(false);
    expect(crossesCard(points, orders)).toBe(false);
    for (const point of points.slice(1, -1)) {
      expect(isInside(point, orders)).toBe(false);
      expect(isInside(point, invoices)).toBe(false);
    }
  });

  it("enters a table from the side its channel ends up on", () => {
    // Stacked tables with the right side blocked: the channel goes left and
    // both ends must attach on the left edge.
    const parent = node("parent", 100, 0);
    const blocker = node("blocker", 420, 0, 280, 600);
    const child = node("child", 120, 320);
    const [edge] = buildErdRelationEdges(
      document([{ name: "parent_child", from: "parent", to: "child" }]),
      [parent, blocker, child],
    );

    const points = parsePath(edge!.path);
    expect(points[0]!.x).toBe(parent.x);
    expect(points.at(-1)!.x).toBe(child.x);
    expect(crossesCard(points, child)).toBe(false);
    expect(crossesCard(points, parent)).toBe(false);
  });
});
