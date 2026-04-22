import { describe, expect, it } from "vitest";
import type { SchemaModelDocument } from "../types/api";
import { addAutomaticRenameHints } from "../features/schema-workbench/schemaRenameHints";

const baselineSchema: SchemaModelDocument = {
  version: 1,
  tables: [
    {
      name: "customers",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
        },
        {
          name: "full_name",
          type: "text",
          nullable: false,
        },
      ],
      indexes: [
        {
          name: "idx_customers_full_name",
          columns: ["full_name"],
          unique: false,
        },
      ],
    },
  ],
  relations: [],
};

describe("addAutomaticRenameHints", () => {
  it("adds same-slot table, column, and index rename hints without mutating the draft", () => {
    const draft: SchemaModelDocument = {
      version: 1,
      tables: [
        {
          name: "accounts",
          columns: [
            baselineSchema.tables[0]!.columns[0]!,
            {
              name: "name",
              type: "text",
              nullable: false,
            },
          ],
          indexes: [
            {
              name: "idx_accounts_name",
              columns: ["name"],
              unique: false,
            },
          ],
        },
      ],
      relations: [],
    };

    const result = addAutomaticRenameHints(draft, baselineSchema);

    expect(result).not.toBe(draft);
    expect(result.tables[0]!.renamedFrom).toBe("customers");
    expect(result.tables[0]!.columns[1]!.renamedFrom).toBe("full_name");
    expect(result.tables[0]!.indexes![0]!.renamedFrom).toBe(
      "idx_customers_full_name",
    );
    expect(draft.tables[0]!.renamedFrom).toBeUndefined();
    expect(draft.tables[0]!.columns[1]!.renamedFrom).toBeUndefined();
    expect(draft.tables[0]!.indexes![0]!.renamedFrom).toBeUndefined();
  });

  it("preserves explicit rename hints and explicit null suppressions", () => {
    const draft: SchemaModelDocument = {
      version: 1,
      tables: [
        {
          name: "accounts",
          renamedFrom: null,
          columns: [
            baselineSchema.tables[0]!.columns[0]!,
            {
              name: "name",
              renamedFrom: "legacy_full_name",
              type: "text",
              nullable: false,
            },
          ],
          indexes: [],
        },
      ],
      relations: [],
    };

    const result = addAutomaticRenameHints(draft, baselineSchema);

    expect(result.tables[0]!.renamedFrom).toBeNull();
    expect(result.tables[0]!.columns[1]!.renamedFrom).toBe("legacy_full_name");
  });

  it("does not infer rename hints when the old name still exists in the draft scope", () => {
    const baseline: SchemaModelDocument = {
      version: 1,
      tables: [
        {
          name: "customers",
          columns: baselineSchema.tables[0]!.columns,
          indexes: [],
        },
        {
          name: "accounts",
          columns: [
            {
              name: "id",
              type: "integer",
              primaryKey: true,
              nullable: false,
            },
          ],
          indexes: [],
        },
      ],
      relations: [],
    };
    const draft: SchemaModelDocument = {
      version: 1,
      tables: [
        {
          name: "accounts",
          columns: [
            {
              name: "id",
              type: "integer",
              primaryKey: true,
              nullable: false,
            },
          ],
          indexes: [],
        },
        {
          name: "customers",
          columns: baselineSchema.tables[0]!.columns,
          indexes: [],
        },
      ],
      relations: [],
    };

    const result = addAutomaticRenameHints(draft, baseline);

    expect(result).toBe(draft);
    expect(result.tables[0]!.renamedFrom).toBeUndefined();
    expect(result.tables[1]!.renamedFrom).toBeUndefined();
  });

  it("cascades table renames to reference columns and relation endpoints in the request model", () => {
    const baseline: SchemaModelDocument = {
      version: 1,
      tables: [
        baselineSchema.tables[0]!,
        {
          name: "orders",
          columns: [
            {
              name: "id",
              type: "integer",
              primaryKey: true,
              nullable: false,
            },
            {
              name: "customer_id",
              type: "reference",
              references: "customers",
              nullable: false,
            },
          ],
          indexes: [],
        },
      ],
      relations: [
        {
          name: "customer_orders",
          type: "1:N",
          from: "customers",
          to: "orders",
          column: "customer_id",
        },
      ],
    };
    const draft: SchemaModelDocument = {
      version: 1,
      tables: [
        {
          ...baseline.tables[0]!,
          name: "accounts",
        },
        baseline.tables[1]!,
      ],
      relations: baseline.relations,
    };

    const result = addAutomaticRenameHints(draft, baseline);

    expect(result.tables[0]!.renamedFrom).toBe("customers");
    expect(result.tables[1]!.columns[1]!.references).toBe("accounts");
    expect(result.relations[0]!.from).toBe("accounts");
    expect(result.relations[0]!.to).toBe("orders");
    expect(draft.tables[1]!.columns[1]!.references).toBe("customers");
    expect(draft.relations[0]!.from).toBe("customers");
  });
});
