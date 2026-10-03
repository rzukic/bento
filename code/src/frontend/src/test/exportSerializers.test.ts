import { describe, expect, it } from "vitest";
import type { ModelColumn, ModelTable } from "../types/api";
import {
  buildExportFilename,
  getOrderedVisibleColumns,
  rowsToCsv,
  rowsToJson,
} from "../features/data-explorer/export/serializers";

const columns: ModelColumn[] = [
  { name: "id", type: "int64", primaryKey: true },
  {
    name: "customer_id",
    displayName: "Customer",
    type: "relation",
    references: { table: "customers", column: "id" },
  },
  { name: "notes", displayName: "Notes", type: "string" },
  { name: "active", displayName: "Active", type: "boolean" },
  { name: "meta", displayName: "Meta", type: "json" },
];

describe("export serializers", () => {
  it("orders visible columns with primary keys first and configured columns next", () => {
    const table: ModelTable = {
      name: "orders",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "status", type: "string" },
        { name: "customer_id", type: "relation", references: "customers" },
        { name: "total", type: "double" },
      ],
    };

    expect(
      getOrderedVisibleColumns(table, ["total"], ["customer_id", "status"]).map(
        (column) => column.name,
      ),
    ).toEqual(["id", "customer_id", "status"]);
  });

  it("serializes CSV with display headers, FK labels, and RFC-4180 escaping", () => {
    const csv = rowsToCsv(
      [
        {
          id: 101,
          customer_id: 7,
          notes: 'Line one,\nthen "quoted"',
          active: 1,
          meta: { priority: "high" },
        },
      ],
      columns,
      { customer_id: { "7": "Acme Co" } },
    );

    expect(csv).toBe(
      'id,Customer,Notes,Active,Meta\r\n101,Acme Co,"Line one,\nthen ""quoted""",TRUE,"{""priority"":""high""}"',
    );
  });

  it("serializes JSON with API-shaped FK ids plus sibling display fields and typed booleans", () => {
    const json = rowsToJson(
      [
        {
          id: 101,
          customer_id: 7,
          notes: "Ready",
          active: "false",
          meta: { priority: "high" },
        },
      ],
      columns,
      { customer_id: { "7": "Acme Co" } },
    );

    expect(JSON.parse(json)).toEqual([
      {
        id: 101,
        customer_id: 7,
        customer_id_display: "Acme Co",
        notes: "Ready",
        active: false,
        meta: { priority: "high" },
      },
    ]);
  });

  it("builds the documented export filename pattern", () => {
    expect(
      buildExportFilename(
        "orders",
        "Pending Orders",
        "csv",
        new Date("2026-04-18T10:15:00Z"),
      ),
    ).toBe("orders-Pending-Orders-2026-04-18.csv");
  });
});
