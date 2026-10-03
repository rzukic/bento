import { describe, expect, it } from "vitest";
import { buildErdTableNodes } from "../features/schema-workbench/visual/erdRouting";
import type { SchemaModelDocument } from "../types/api";

function table(name: string, layout?: { x: number; y: number }) {
  return {
    name,
    columns: [{ name: "id", type: "integer", primaryKey: true }],
    ...(layout ? { layout } : {}),
  };
}

function junctionDocument(left: { x: number; y: number }, right: { x: number; y: number }) {
  return {
    tables: [
      table("orders", left),
      table("products", right),
      {
        name: "_jt_orders_products",
        columns: [
          { name: "order_id", type: "reference", references: "orders", primaryKey: true },
          { name: "product_id", type: "reference", references: "products", primaryKey: true },
        ],
      },
    ],
    relations: [
      {
        name: "product_orders",
        type: "N:M",
        from: "products",
        to: "orders",
        junctionTable: "_jt_orders_products",
      },
    ],
  } as unknown as SchemaModelDocument;
}

describe("ERD placement of junction tables without a saved position", () => {
  it("puts the junction in the gap between the two tables when it fits", () => {
    const nodes = buildErdTableNodes(junctionDocument({ x: 0, y: 0 }, { x: 800, y: 0 }));
    const orders = nodes[0];
    const products = nodes[1];
    const junction = nodes[2];
    expect(junction.x).toBeGreaterThanOrEqual(orders.x + orders.width);
    expect(junction.x + junction.width).toBeLessThanOrEqual(products.x);
  });

  it("puts the junction beside the right-hand table when the gap is too narrow", () => {
    const nodes = buildErdTableNodes(junctionDocument({ x: 0, y: 0 }, { x: 400, y: 0 }));
    const products = nodes[1];
    const junction = nodes[2];
    expect(junction.x).toBeGreaterThan(products.x + products.width);
  });
});

describe("ERD drop and line drawing", () => {
  it("moves a table dropped onto another to the nearest free spot", async () => {
    const { findFreeErdPosition } = await import("../features/schema-workbench/visual/erdRouting");
    const nodes = buildErdTableNodes(junctionDocument({ x: 0, y: 0 }, { x: 800, y: 0 }));
    const free = findFreeErdPosition(nodes, "_jt_orders_products", 20, 20);
    const orders = nodes[0];
    const junction = nodes[2];
    const overlaps =
      free.x < orders.x + orders.width &&
      orders.x < free.x + junction.width &&
      free.y < orders.y + orders.height &&
      orders.y < free.y + junction.height;
    expect(overlaps).toBe(false);
    expect(findFreeErdPosition(nodes, "_jt_orders_products", 400, 600)).toEqual({ x: 400, y: 600 });
  });

  it("draws a run that doubles back on itself as one straight segment", async () => {
    const { smoothErdPath } = await import("../features/schema-workbench/visual/erdRouting");
    const smoothed = smoothErdPath("M 0 0 L 100 0 L 100 50 L 100 20 L 100 80 L 200 80");
    // One bend down at x=100, one bend right at y=80: two curves, no loops.
    expect(smoothed.match(/Q/g)).toHaveLength(2);
  });
});
