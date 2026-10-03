import { describe, expect, it } from "vitest";
import {
  getDisplayColumnForTable,
  getSecondaryColumnForTable,
} from "../features/data-explorer/components/editors/referenceMetadata";
import { removeColumn, updateColumn } from "../features/schema-workbench/visualSchemaDraft";
import type { ModelTable, SchemaModelDocument } from "../types/api";

const products: ModelTable = {
  name: "products",
  columns: [
    { name: "id", type: "integer", primaryKey: true },
    { name: "sku", type: "text" },
    { name: "name", type: "text" },
  ],
};

describe("record label", () => {
  it("names records by the first text column when the table sets nothing", () => {
    expect(getDisplayColumnForTable(products)).toBe("sku");
    expect(getSecondaryColumnForTable(products)).toBeNull();
  });

  it("uses the table's record label and its secondary column", () => {
    const labelled = { ...products, recordLabel: { column: "name", secondary: "sku" } };
    expect(getDisplayColumnForTable(labelled)).toBe("name");
    expect(getSecondaryColumnForTable(labelled)).toBe("sku");
  });

  it("ignores a record label that points at a missing column", () => {
    const stale = { ...products, recordLabel: { column: "title", secondary: "code" } };
    expect(getDisplayColumnForTable(stale)).toBe("sku");
    expect(getSecondaryColumnForTable(stale)).toBeNull();
  });

  it("follows column renames and drops removed columns in the designer", () => {
    const document = {
      tables: [
        {
          name: "products",
          columns: products.columns,
          recordLabel: { column: "name", secondary: "sku" },
        },
      ],
      relations: [],
    } as unknown as SchemaModelDocument;

    const renamed = updateColumn(document, "products", "name", { name: "title" });
    expect(renamed.tables[0]!.recordLabel).toEqual({ column: "title", secondary: "sku" });

    const withoutSku = removeColumn(renamed, "products", "sku");
    expect(withoutSku.tables[0]!.recordLabel).toEqual({ column: "title" });

    const withoutTitle = removeColumn(withoutSku, "products", "title");
    expect(withoutTitle.tables[0]!.recordLabel).toBeUndefined();
  });
});
