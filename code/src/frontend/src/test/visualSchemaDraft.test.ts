import { describe, expect, it } from "vitest";
import type { SchemaModelDocument } from "../types/api";
import { formatSchemaDraftValueForEditor } from "../features/schema-workbench/schemaDraftPresentation";
import {
  addColumn,
  addIndex,
  addRelation,
  addTable,
  clearTableLayout,
  convertManyToManyRelationToOneToMany,
  convertOneToManyRelationToManyToMany,
  createManyToManyRelation,
  createOneToManyRelation,
  createStarterTable,
  deleteRelationWithArtifacts,
  deleteTableWithDependents,
  duplicateTable,
  getDefaultManyToManyRelationDraft,
  getDefaultOneToManyRelationDraft,
  getTableLayout,
  getVisualDraftPathForColumn,
  getVisualDraftPathForIndex,
  getVisualDraftPathForRelation,
  getVisualDraftPathForTable,
  removeColumn,
  removeIndex,
  removeRelation,
  removeTable,
  renameRelation,
  renameTable,
  reorderColumn,
  switchManyToManyRelationDirection,
  setTableLayout,
  updateManyToManyRelation,
  switchOneToManyRelationDirection,
  updateColumn,
  updateIndex,
  updateOneToManyRelation,
  updateRelation,
  updateTable,
  validateVisualSchemaDraft,
} from "../features/schema-workbench/visualSchemaDraft";

const baseSchema: SchemaModelDocument = {
  version: 2,
  tables: [
    {
      name: "customers",
      displayName: "Customers",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
          customColumnMeta: { source: "seed" },
        },
        { name: "name", type: "text", nullable: false },
      ],
      indexes: [
        {
          name: "idx_customers_name",
          columns: ["name"],
          customIndexMeta: true,
        },
      ],
      customTableMeta: { owner: "sales" },
    },
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
      customRelationMeta: "kept",
    },
  ],
  customDocumentMeta: "preserved",
};

describe("visual schema draft helpers", () => {
  it("stores table layout metadata without losing extension fields", () => {
    const withLayout = setTableLayout(baseSchema, "customers", {
      x: 120,
      y: 80,
      color: "#2563eb",
      lane: "crm",
    });

    expect(withLayout).not.toBe(baseSchema);
    expect(withLayout.customDocumentMeta).toBe("preserved");
    expect(withLayout.tables[0].customTableMeta).toEqual({ owner: "sales" });
    expect(withLayout.tables[0].columns[0].customColumnMeta).toEqual({
      source: "seed",
    });
    expect(getTableLayout(withLayout.tables[0])).toEqual({
      x: 120,
      y: 80,
      color: "#2563eb",
      lane: "crm",
    });
    expect(getTableLayout(baseSchema.tables[0])).toBeNull();

    const moved = setTableLayout(withLayout, "customers", { x: 200, y: 160 });
    expect(getTableLayout(moved.tables[0])).toEqual({
      x: 200,
      y: 160,
      color: "#2563eb",
      lane: "crm",
    });

    const cleared = clearTableLayout(moved, "customers");
    expect(getTableLayout(cleared.tables[0])).toBeNull();
    expect(cleared.tables[0].customTableMeta).toEqual({ owner: "sales" });

    const roundTripped = JSON.parse(
      formatSchemaDraftValueForEditor(withLayout),
    ) as SchemaModelDocument;
    expect(roundTripped.customDocumentMeta).toBe("preserved");
    expect(roundTripped.tables[0].layout).toEqual({
      x: 120,
      y: 80,
      color: "#2563eb",
      lane: "crm",
    });
    expect(roundTripped.tables[0].customTableMeta).toEqual({ owner: "sales" });
  });

  it("mutates tables, columns, indexes, and relations immutably", () => {
    const withTable = addTable(baseSchema, {
      name: "invoices",
      columns: [{ name: "id", type: "integer", primaryKey: true }],
      indexes: [],
      customTableMeta: "new",
    });
    const renamedTable = updateTable(withTable, "invoices", {
      displayName: "Invoices",
    });
    const withColumn = addColumn(
      renamedTable,
      "invoices",
      { name: "total", type: "currency", nullable: false },
      1,
    );
    const updatedColumn = updateColumn(withColumn, "invoices", "total", {
      unique: true,
    });
    const reorderedColumn = reorderColumn(updatedColumn, "invoices", "total", 0);
    const withIndex = addIndex(reorderedColumn, "invoices", {
      name: "idx_invoices_total",
      columns: ["total"],
    });
    const updatedIndex = updateIndex(withIndex, "invoices", "idx_invoices_total", {
      unique: true,
    });
    const withRelation = addRelation(updatedIndex, {
      name: "customer_invoices",
      type: "1:N",
      from: "customers",
      to: "invoices",
      column: "customer_id",
      customRelationMeta: "new",
    });
    const updatedRelation = updateRelation(withRelation, "customer_invoices", {
      toAccessor: "customer",
    });

    expect(baseSchema.tables.map((table) => table.name)).toEqual([
      "customers",
      "orders",
    ]);
    expect(updatedRelation.tables).toHaveLength(3);
    expect(updatedRelation.tables[2]).toMatchObject({
      name: "invoices",
      displayName: "Invoices",
      customTableMeta: "new",
    });
    expect(updatedRelation.tables[2].columns.map((column) => column.name)).toEqual([
      "total",
      "id",
    ]);
    expect(updatedRelation.tables[2].columns[0]).toMatchObject({
      type: "currency",
      unique: true,
    });
    expect(updatedRelation.tables[2].indexes?.[0]).toMatchObject({
      name: "idx_invoices_total",
      unique: true,
    });
    expect(updatedRelation.relations[1]).toMatchObject({
      name: "customer_invoices",
      customRelationMeta: "new",
      toAccessor: "customer",
    });

    const removedRelation = removeRelation(updatedRelation, "customer_invoices");
    const removedIndex = removeIndex(
      removedRelation,
      "invoices",
      "idx_invoices_total",
    );
    const removedColumn = removeColumn(removedIndex, "invoices", "total");
    const removedTable = removeTable(removedColumn, "invoices");

    expect(removedTable).toEqual(baseSchema);
  });

  it("creates starter tables and keeps table renames explicit across dependencies", () => {
    const created = createStarterTable(baseSchema, { x: 640, y: 320 });

    expect(created.tableName).toBe("new_table");
    expect(created.document.tables[2]).toMatchObject({
      name: "new_table",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
        },
      ],
      indexes: [],
      layout: { x: 640, y: 320 },
    });

    const renamed = renameTable(
      created.document,
      "customers",
      "accounts",
      "customers",
    );

    expect(renamed.tables[0]).toMatchObject({
      name: "accounts",
      renamedFrom: "customers",
    });
    expect(renamed.tables[1].columns[1]).toMatchObject({
      type: "reference",
      references: "accounts",
    });
    expect(renamed.relations[0]).toMatchObject({
      from: "accounts",
      to: "orders",
    });

    const clearedHint = renameTable(
      renamed,
      "accounts",
      "customers",
      null,
    );
    expect(clearedHint.tables[0].renamedFrom).toBeUndefined();
  });

  it("duplicates tables with conflict-free schema object names", () => {
    const withSelfRelation: SchemaModelDocument = {
      ...baseSchema,
      tables: baseSchema.tables.map((table) =>
        table.name === "customers"
          ? {
              ...table,
              columns: [
                ...table.columns,
                {
                  name: "parent_id",
                  type: "reference",
                  references: "customers",
                },
              ],
            }
          : table,
      ),
      relations: [
        ...baseSchema.relations,
        {
          name: "customer_parent",
          type: "1:N",
          from: "customers",
          to: "customers",
          column: "parent_id",
        },
      ],
    };

    const duplicated = duplicateTable(withSelfRelation, "customers");
    const copiedTable = duplicated.document.tables.find(
      (table) => table.name === "customers_copy",
    );

    expect(duplicated.tableName).toBe("customers_copy");
    expect(copiedTable).toMatchObject({
      name: "customers_copy",
      displayName: "Customers Copy",
      columns: [
        expect.objectContaining({ name: "id" }),
        expect.objectContaining({ name: "name" }),
        expect.objectContaining({
          name: "parent_id",
          references: "customers_copy",
        }),
      ],
    });
    expect(copiedTable?.indexes?.[0]).toMatchObject({
      name: "idx_customers_copy_name",
      columns: ["name"],
    });
    expect(duplicated.document.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_parent_copy",
        from: "customers_copy",
        to: "customers_copy",
      }),
    );
    expect(baseSchema.tables).toHaveLength(2);
  });

  it("creates 1:N relations with a new reference column and supporting index", () => {
    const withoutRelation: SchemaModelDocument = {
      ...baseSchema,
      tables: baseSchema.tables.map((table) =>
        table.name === "orders"
          ? {
              ...table,
              columns: table.columns.filter(
                (column) => column.name !== "customer_id",
              ),
            }
          : table,
      ),
      relations: [],
    };
    const defaults = getDefaultOneToManyRelationDraft(
      withoutRelation,
      "customers",
      "orders",
    );
    const result = createOneToManyRelation(withoutRelation, {
      fromTableName: "customers",
      toTableName: "orders",
      ...defaults,
    });

    expect(result.issues).toEqual([]);
    expect(result.relationName).toBe("customer_orders");
    expect(result.document.relations[0]).toEqual({
      name: "customer_orders",
      type: "1:N",
      from: "customers",
      to: "orders",
      column: "customer_id",
      fromAccessor: "orders",
      toAccessor: "customer",
    });
    expect(result.document.tables[1].columns).toContainEqual(
      expect.objectContaining({
        name: "customer_id",
        type: "reference",
        references: "customers",
        relationName: "customer_orders",
      }),
    );
    expect(result.document.tables[1].indexes).toContainEqual({
      name: "idx_orders_customer_id",
      columns: ["customer_id"],
      unique: false,
    });
  });

  it("creates 1:N relations by intentionally reusing an existing reference column", () => {
    const result = createOneToManyRelation(
      {
        ...baseSchema,
        relations: [],
      },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_orders",
        reuseColumnName: "customer_id",
        fromAccessor: "orders",
        toAccessor: "customer",
        indexName: "idx_orders_customer_id",
      },
    );

    expect(result.issues).toEqual([]);
    expect(result.document.tables[1].columns).toHaveLength(2);
    expect(result.document.tables[1].indexes).toContainEqual({
      name: "idx_orders_customer_id",
      columns: ["customer_id"],
      unique: false,
    });
    expect(result.document.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        column: "customer_id",
      }),
    );
  });

  it("creates N:M relations with a visible editable junction table", () => {
    const document = { ...baseSchema, relations: [] };
    const defaults = getDefaultManyToManyRelationDraft(
      document,
      "customers",
      "orders",
    );
    const result = createManyToManyRelation(document, {
      fromTableName: "customers",
      toTableName: "orders",
      ...defaults,
      layout: { x: 300, y: 260 },
    });

    expect(result.issues).toEqual([]);
    expect(result.relationName).toBe("customer_orders");
    expect(result.document.tables).toContainEqual(
      expect.objectContaining({
        name: "_jt_customers_orders",
        layout: { x: 300, y: 260 },
        columns: [
          expect.objectContaining({
            name: "customer_id",
            type: "reference",
            references: "customers",
            primaryKey: true,
          }),
          expect.objectContaining({
            name: "order_id",
            type: "reference",
            references: "orders",
            primaryKey: true,
          }),
          expect.objectContaining({
            name: "created_at",
            type: "datetime",
            nullable: true,
          }),
        ],
      }),
    );
    expect(result.document.relations).toContainEqual({
      name: "customer_orders",
      type: "N:M",
      from: "customers",
      to: "orders",
      fromColumn: "customer_id",
      toColumn: "order_id",
      junctionTable: "_jt_customers_orders",
      fromAccessor: "orders",
      toAccessor: "customers",
    });
  });

  it("allows renamed N:M junction tables and extra junction columns", () => {
    const created = createManyToManyRelation(
      { ...baseSchema, relations: [] },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_order_links",
        junctionTableName: "customer_order_links",
        fromColumnName: "customer_ref",
        toColumnName: "order_ref",
        fromAccessor: "linked_orders",
        toAccessor: "linked_customers",
      },
    );

    const withExtraColumn = addColumn(
      created.document,
      "customer_order_links",
      { name: "role", type: "text", nullable: true },
    );

    expect(created.issues).toEqual([]);
    expect(
      withExtraColumn.tables
        .find((table) => table.name === "customer_order_links")
        ?.columns.map((column) => column.name),
    ).toEqual(["customer_ref", "order_ref", "created_at", "role"]);
    expect(withExtraColumn.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_order_links",
        type: "N:M",
        junctionTable: "customer_order_links",
        fromColumn: "customer_ref",
        toColumn: "order_ref",
      }),
    );
  });

  it("rejects N:M relation and junction naming conflicts before preview", () => {
    const result = createManyToManyRelation(baseSchema, {
      fromTableName: "customers",
      toTableName: "orders",
      relationName: "customer_orders",
      junctionTableName: "orders",
      fromColumnName: "customer_id",
      toColumnName: "customer_id",
      fromAccessor: "customer_orders",
      toAccessor: "customer",
    });

    expect(result.document).toBe(baseSchema);
    expect(result.issues).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ code: "visual_duplicate_relation_name" }),
        expect.objectContaining({ code: "visual_duplicate_table_name" }),
        expect.objectContaining({ code: "visual_duplicate_column_name" }),
        expect.objectContaining({ code: "visual_relation_accessor_collision" }),
      ]),
    );
  });

  it("rejects visual relation name, column, and accessor conflicts before preview", () => {
    const result = createOneToManyRelation(baseSchema, {
      fromTableName: "customers",
      toTableName: "orders",
      relationName: "customer_orders",
      columnName: "customer_id",
      fromAccessor: "customer_orders",
      toAccessor: "customer",
      indexName: "idx_orders_customer_id",
    });

    expect(result.document).toBe(baseSchema);
    expect(result.issues).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ code: "visual_duplicate_relation_name" }),
        expect.objectContaining({ code: "visual_duplicate_column_name" }),
        expect.objectContaining({ code: "visual_relation_accessor_collision" }),
      ]),
    );
  });

  it("creates self-referential 1:N relations with explicit accessors", () => {
    const result = createOneToManyRelation(baseSchema, {
      fromTableName: "customers",
      toTableName: "customers",
      relationName: "customer_children",
      columnName: "parent_customer_id",
      fromAccessor: "children",
      toAccessor: "parent_customer",
      indexName: "idx_customers_parent_customer_id",
    });

    expect(result.issues).toEqual([]);
    expect(result.document.tables[0].columns).toContainEqual(
      expect.objectContaining({
        name: "parent_customer_id",
        type: "reference",
        references: "customers",
      }),
    );
    expect(result.document.relations).toContainEqual({
      name: "customer_children",
      type: "1:N",
      from: "customers",
      to: "customers",
      column: "parent_customer_id",
      fromAccessor: "children",
      toAccessor: "parent_customer",
    });
  });

  it("switches draft-only relation direction and cleans up generated reference artifacts", () => {
    const created = createOneToManyRelation(
      {
        ...baseSchema,
        tables: baseSchema.tables.map((table) =>
          table.name === "orders"
            ? {
                ...table,
                columns: table.columns.filter(
                  (column) => column.name !== "customer_id",
                ),
              }
            : table,
        ),
        relations: [],
      },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_orders",
        columnName: "customer_id",
        fromAccessor: "orders",
        toAccessor: "customer",
        indexName: "idx_orders_customer_id",
      },
    );

    const switched = switchOneToManyRelationDirection(
      created.document,
      "customer_orders",
      { ...created.document, relations: [] },
    );

    expect(switched.issues).toEqual([]);
    expect(
      switched.document.tables
        .find((table) => table.name === "orders")
        ?.columns.some((column) => column.name === "customer_id"),
    ).toBe(false);
    expect(switched.document.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        from: "orders",
        to: "customers",
        column: "order_id",
      }),
    );
    expect(switched.document.tables[0].columns).toContainEqual(
      expect.objectContaining({
        name: "order_id",
        type: "reference",
        references: "orders",
      }),
    );
  });

  it("converts a 1:N relation into an N:M relation with a visible junction table", () => {
    const result = convertOneToManyRelationToManyToMany(baseSchema, {
      relationName: "customer_orders",
      layout: { x: 320, y: 240 },
    });

    expect(result.issues).toEqual([]);
    expect(
      result.document.tables.find((table) => table.name === "orders")?.columns,
    ).toEqual([
      expect.objectContaining({
        name: "id",
        type: "integer",
      }),
    ]);
    expect(result.document.tables).toContainEqual(
      expect.objectContaining({
        name: "_jt_customers_orders",
        layout: { x: 320, y: 240 },
        columns: [
          expect.objectContaining({
            name: "customer_id",
            references: "customers",
            primaryKey: true,
          }),
          expect.objectContaining({
            name: "order_id",
            references: "orders",
            primaryKey: true,
          }),
          expect.objectContaining({
            name: "created_at",
            type: "datetime",
            nullable: true,
          }),
        ],
      }),
    );
    expect(result.document.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        type: "N:M",
        from: "customers",
        to: "orders",
        fromColumn: "customer_id",
        toColumn: "order_id",
        junctionTable: "_jt_customers_orders",
      }),
    );
  });

  it("converts an N:M relation back into a 1:N relation and removes the visible junction table", () => {
    const manyToMany = createManyToManyRelation(
      { ...baseSchema, relations: [] },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_orders",
        junctionTableName: "_jt_customers_orders",
        fromColumnName: "customer_id",
        toColumnName: "order_id",
        fromAccessor: "orders",
        toAccessor: "customers",
      },
    );

    const result = convertManyToManyRelationToOneToMany(manyToMany.document, {
      relationName: "customer_orders",
    });

    expect(result.issues).toEqual([]);
    expect(
      result.document.tables.some((table) => table.name === "_jt_customers_orders"),
    ).toBe(false);
    expect(result.document.tables.find((table) => table.name === "orders")?.columns).toContainEqual(
      expect.objectContaining({
        name: "customer_id",
        type: "reference",
        references: "customers",
      }),
    );
    expect(result.document.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        type: "1:N",
        column: "customer_id",
      }),
    );
  });

  it("updates N:M endpoints while retargeting visible junction columns", () => {
    const manyToMany = createManyToManyRelation(
      { ...baseSchema, relations: [] },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_orders",
        junctionTableName: "_jt_customers_orders",
        fromColumnName: "customer_id",
        toColumnName: "order_id",
        fromAccessor: "orders",
        toAccessor: "customers",
      },
    );

    const result = updateManyToManyRelation(manyToMany.document, {
      relationName: "customer_orders",
      fromTableName: "orders",
      toTableName: "customers",
      fromAccessor: "customers",
      toAccessor: "orders",
    });

    expect(result.issues).toEqual([]);
    expect(result.document.relations[0]).toMatchObject({
      name: "customer_orders",
      type: "N:M",
      from: "orders",
      to: "customers",
      fromColumn: "customer_id",
      toColumn: "order_id",
      junctionTable: "_jt_customers_orders",
      fromAccessor: "customers",
      toAccessor: "orders",
    });
    expect(
      result.document.tables.find((table) => table.name === "_jt_customers_orders")?.columns,
    ).toEqual([
      expect.objectContaining({
        name: "customer_id",
        references: "orders",
      }),
      expect.objectContaining({
        name: "order_id",
        references: "customers",
      }),
      expect.objectContaining({ name: "created_at" }),
    ]);
  });

  it("switches N:M direction by swapping endpoints and junction columns", () => {
    const manyToMany = createManyToManyRelation(
      { ...baseSchema, relations: [] },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_orders",
        junctionTableName: "_jt_customers_orders",
        fromColumnName: "customer_id",
        toColumnName: "order_id",
        fromAccessor: "orders",
        toAccessor: "customers",
      },
    );

    const result = switchManyToManyRelationDirection(
      manyToMany.document,
      "customer_orders",
    );

    expect(result.issues).toEqual([]);
    expect(result.document.relations[0]).toMatchObject({
      name: "customer_orders",
      type: "N:M",
      from: "orders",
      to: "customers",
      fromColumn: "order_id",
      toColumn: "customer_id",
      fromAccessor: "customers",
      toAccessor: "orders",
    });
    expect(
      result.document.tables.find((table) => table.name === "_jt_customers_orders")?.columns,
    ).toEqual([
      expect.objectContaining({
        name: "customer_id",
        references: "customers",
      }),
      expect.objectContaining({
        name: "order_id",
        references: "orders",
      }),
      expect.objectContaining({ name: "created_at" }),
    ]);
  });

  it("renames N:M visible junction tables and columns together with relation metadata", () => {
    const manyToMany = createManyToManyRelation(
      { ...baseSchema, relations: [] },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_orders",
        junctionTableName: "_jt_customers_orders",
        fromColumnName: "customer_id",
        toColumnName: "order_id",
        fromAccessor: "orders",
        toAccessor: "customers",
      },
    );

    const result = updateManyToManyRelation(manyToMany.document, {
      relationName: "customer_orders",
      junctionTableName: "customer_order_links",
      fromColumnName: "source_customer_id",
      toColumnName: "target_order_id",
    });

    expect(result.issues).toEqual([]);
    expect(result.document.tables.some((table) => table.name === "_jt_customers_orders")).toBe(false);
    expect(result.document.tables).toContainEqual(
      expect.objectContaining({
        name: "customer_order_links",
        columns: [
          expect.objectContaining({
            name: "source_customer_id",
            references: "customers",
          }),
          expect.objectContaining({
            name: "target_order_id",
            references: "orders",
          }),
          expect.objectContaining({ name: "created_at" }),
        ],
      }),
    );
    expect(result.document.relations[0]).toMatchObject({
      junctionTable: "customer_order_links",
      fromColumn: "source_customer_id",
      toColumn: "target_order_id",
    });
  });

  it("deletes a 1:N relation and removes its reference column and supporting index", () => {
    const document = addIndex(baseSchema, "orders", {
      name: "idx_orders_customer_id",
      columns: ["customer_id"],
      unique: false,
    });

    const result = deleteRelationWithArtifacts(document, "customer_orders");

    expect(result.issues).toEqual([]);
    expect(result.document.relations).toEqual([]);
    expect(
      result.document.tables.find((table) => table.name === "orders")?.columns,
    ).not.toContainEqual(expect.objectContaining({ name: "customer_id" }));
    expect(
      result.document.tables.find((table) => table.name === "orders")?.indexes,
    ).not.toContainEqual(
      expect.objectContaining({ name: "idx_orders_customer_id" }),
    );
  });

  it("deletes an N:M relation and removes its visible junction table", () => {
    const manyToMany = createManyToManyRelation(
      { ...baseSchema, relations: [] },
      {
        fromTableName: "customers",
        toTableName: "orders",
        relationName: "customer_orders",
        junctionTableName: "_jt_customers_orders",
        fromColumnName: "customer_id",
        toColumnName: "order_id",
        fromAccessor: "orders",
        toAccessor: "customers",
      },
    );

    const result = deleteRelationWithArtifacts(manyToMany.document, "customer_orders");

    expect(result.issues).toEqual([]);
    expect(result.document.relations).toEqual([]);
    expect(
      result.document.tables.some((table) => table.name === "_jt_customers_orders"),
    ).toBe(false);
  });

  it("retargets committed relations by removing the old reference column from the draft", () => {
    const retargeted = updateOneToManyRelation(baseSchema, {
      baselineDocument: baseSchema,
      relationName: "customer_orders",
      fromTableName: "orders",
      toTableName: "customers",
      columnName: "order_id",
      fromAccessor: "customers",
      toAccessor: "order",
    });

    expect(retargeted.issues).toEqual([]);
    expect(
      retargeted.document.tables[1].columns.some(
        (column) => column.name === "customer_id",
      ),
    ).toBe(false);
    expect(retargeted.document.tables[0].columns).toContainEqual(
      expect.objectContaining({
        name: "order_id",
        type: "reference",
        references: "orders",
      }),
    );
    expect(retargeted.document.relations[0]).toMatchObject({
      from: "orders",
      to: "customers",
      column: "order_id",
    });
  });

  it("deletes tables with dependent relation and reference-column cleanup", () => {
    const withExternalReference = addColumn(baseSchema, "orders", {
      name: "backup_customer_id",
      type: "reference",
      references: "customers",
    });

    const deleted = deleteTableWithDependents(
      {
        ...withExternalReference,
        tables: withExternalReference.tables.map((table) =>
          table.name === "orders"
            ? {
                ...table,
                indexes: [
                  {
                    name: "idx_orders_backup_customer_id",
                    columns: ["backup_customer_id"],
                  },
                ],
              }
            : table,
        ),
      },
      "customers",
    );

    expect(deleted.tables.map((table) => table.name)).toEqual(["orders"]);
    expect(deleted.relations).toEqual([]);
    expect(deleted.tables[0].columns.map((column) => column.name)).toEqual(["id"]);
    expect(deleted.tables[0].indexes).toEqual([]);
  });

  it("returns validation-friendly issues for malformed visual drafts", () => {
    const invalidSchema: SchemaModelDocument = {
      version: 1,
      tables: [
        {
          name: "accounts",
          layout: { x: "left", y: 10, color: 7 },
          columns: [
            { name: "id", type: "integer" },
            { name: "id", type: "text" },
            { name: "owner_id", type: "reference", references: "users" },
            { name: "status", type: "enum", values: [] },
          ],
          indexes: [
            { name: "idx_missing", columns: ["missing_column"] },
            { name: "idx_missing", columns: ["id"] },
          ],
        },
        {
          name: "accounts",
          columns: [{ name: "", type: "text" }],
          indexes: [],
        },
      ],
      relations: [
        {
          name: "account_owner",
          type: "1:N",
          from: "users",
          to: "accounts",
          column: "owner_id",
        },
        {
          name: "account_owner",
          type: "1:N",
          from: "accounts",
          to: "accounts",
          column: "missing_column",
        },
      ],
    };

    const issues = validateVisualSchemaDraft(invalidSchema);
    const issueCodes = issues.map((issue) => issue.code);

    expect(issueCodes).toEqual(
      expect.arrayContaining([
        "visual_invalid_table_layout_position",
        "visual_invalid_table_layout_color",
        "visual_duplicate_table_name",
        "visual_duplicate_column_name",
        "visual_missing_reference_target",
        "visual_enum_values_required",
        "visual_duplicate_index_name",
        "visual_missing_index_column",
        "visual_column_name_required",
        "visual_missing_relation_from_table",
        "visual_missing_relation_column",
        "visual_duplicate_relation_name",
      ]),
    );
    expect(issues).toContainEqual(
      expect.objectContaining({
        code: "visual_missing_reference_target",
        path: "tables[0].columns[2].references",
        severity: "error",
      }),
    );
  });

  it("clears layout color while preserving x/y when color is set to null", () => {
    const colored = setTableLayout(baseSchema, "customers", {
      x: 120,
      y: 80,
      color: "#2563eb",
    });
    const clearedColor = setTableLayout(colored, "customers", {
      x: 120,
      y: 80,
      color: null,
    });

    const layout = getTableLayout(clearedColor.tables[0]);
    expect(layout?.x).toBe(120);
    expect(layout?.y).toBe(80);
    expect(layout?.color).toBeNull();
  });

  it("renames a relation and rewrites column.relationName references", () => {
    const documentWithLink: SchemaModelDocument = {
      ...baseSchema,
      tables: baseSchema.tables.map((table) =>
        table.name === "orders"
          ? {
              ...table,
              columns: table.columns.map((column) =>
                column.name === "customer_id"
                  ? { ...column, relationName: "customer_orders" }
                  : column,
              ),
            }
          : table,
      ),
    };

    const renamed = renameRelation(
      documentWithLink,
      "customer_orders",
      "customer_orders_renamed",
    );

    expect(renamed.issues).toEqual([]);
    expect(renamed.relationName).toBe("customer_orders_renamed");
    expect(
      renamed.document.relations.map((relation) => relation.name),
    ).toContain("customer_orders_renamed");
    const ordersTable = renamed.document.tables.find(
      (table) => table.name === "orders",
    );
    expect(
      ordersTable?.columns.find((column) => column.name === "customer_id")
        ?.relationName,
    ).toBe("customer_orders_renamed");
  });

  it("rejects relation rename when the new name collides with another relation", () => {
    const documentWithTwo: SchemaModelDocument = {
      ...baseSchema,
      relations: [
        ...baseSchema.relations,
        {
          name: "rel_b",
          type: "1:N",
          from: "customers",
          to: "orders",
          column: "customer_id",
          fromAccessor: "ordersB",
          toAccessor: "customerB",
        },
      ],
    };

    const renamed = renameRelation(documentWithTwo, "customer_orders", "rel_b");
    expect(renamed.relationName).toBe("customer_orders");
    expect(renamed.issues).toContainEqual(
      expect.objectContaining({ code: "visual_relation_name_taken" }),
    );
    expect(renamed.document).toBe(documentWithTwo);
  });

  it("rejects empty relation rename without changing the document", () => {
    const result = renameRelation(baseSchema, "customer_orders", "   ");
    expect(result.relationName).toBe("customer_orders");
    expect(result.issues).toContainEqual(
      expect.objectContaining({ code: "visual_relation_name_required" }),
    );
    expect(result.document).toBe(baseSchema);
  });

  it("resolves schema-object paths for future visual diagnostics", () => {
    expect(getVisualDraftPathForTable(baseSchema, "orders")).toBe("tables[1]");
    expect(getVisualDraftPathForColumn(baseSchema, "orders", "customer_id")).toBe(
      "tables[1].columns[1]",
    );
    expect(getVisualDraftPathForRelation(baseSchema, "customer_orders")).toBe(
      "relations[0]",
    );
    expect(getVisualDraftPathForIndex(baseSchema, "customers", "idx_customers_name")).toBe(
      "tables[0].indexes[0]",
    );
    expect(getVisualDraftPathForColumn(baseSchema, "orders", "missing")).toBeNull();
  });
});
