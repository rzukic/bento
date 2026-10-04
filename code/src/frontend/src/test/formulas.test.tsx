import { CompletionContext } from "@codemirror/autocomplete";
import { ensureSyntaxTree } from "@codemirror/language";
import { EditorState } from "@codemirror/state";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { FormulaFunction } from "../api/formulas";
import { FormulaField } from "../features/formulas/FormulaField";
import { renderCellValue } from "../features/data-explorer/components/cellValue";
import { FormulaEditor } from "../features/formulas/FormulaEditor";
import { valueTypeOf } from "../lib/columns";
import { formulaCompletions, formulaLanguage, formulaLinks } from "../features/formulas/formulaLanguage";
import type { SchemaColumn, SchemaModelDocument } from "../types/api";

const api = vi.hoisted(() => ({
  validateFormula: vi.fn(),
  previewFormula: vi.fn(),
  loadFormulaFunctions: vi.fn(),
}));
vi.mock("../api/formulas", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/formulas")>()), ...api }));

const functions: FormulaFunction[] = [
  { name: "IF", category: "Logic", signature: "IF(condition, then, [else])", description: "Returns then or else.", example: "IF(a, 1, 2)", returns: "any" },
  { name: "ROUND", category: "Math", signature: "ROUND(number, [digits])", description: "Rounds.", example: "ROUND(1.5)", returns: "number" },
];

const columns: SchemaColumn[] = [
  { name: "id", type: "integer", primaryKey: true },
  { name: "booked_on", type: "date", displayName: "Booked on" },
  { name: "amount", type: "currency" },
  { name: "meta", type: "json" },
  { name: "net", type: "computed", formula: "amount" },
];

const model: Pick<SchemaModelDocument, "tables" | "relations"> = {
  tables: [
    { name: "transactions", columns: [...columns, { name: "account_id", type: "reference" as const, references: "accounts" }] },
    { name: "accounts", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] },
    { name: "tags", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "label", type: "text" }] },
  ],
  relations: [
    { name: "account_transactions", type: "1:N", from: "accounts", to: "transactions", column: "account_id" },
    { name: "tag_transactions", type: "N:M", from: "tags", to: "transactions" },
  ],
};

function complete(doc: string, pos = doc.length, table = "transactions") {
  const own = model.tables.find((t) => t.name === table)!.columns;
  const source = formulaCompletions(own, functions, formulaLinks(model, table));
  return source(new CompletionContext(EditorState.create({ doc }), pos, false));
}

describe("formula autocomplete", () => {
  it("offers columns with a value of their own and functions for a word", () => {
    const result = complete("= am");
    expect(result?.from).toBe(2);
    const labels = result!.options.map((option) => option.label);
    expect(labels).toEqual(["id", "booked_on", "amount", "net", "account_id", "account", "tags", "IF", "ROUND"]);
    expect(result!.options.find((option) => option.label === "ROUND")?.apply).toBe("ROUND(");
  });

  it("offers the linked table's columns after a link and a dot", () => {
    const lookup = complete("= account.na");
    expect(lookup?.from).toBe(10);
    expect(lookup!.options.map((option) => option.label)).toEqual(["id", "name"]);

    const rows = complete("= SUM(transactions.", undefined, "accounts");
    expect(rows!.options.map((option) => option.label)).toContain("amount");
    expect(complete("= nothing.x")).toBeNull();
  });

  it("finds links like the server: reference columns, 1:N and N:M relations", () => {
    expect(formulaLinks(model, "transactions").map((l) => `${l.names.join("|")}>${l.target}${l.toMany ? "*" : ""}`)).toEqual([
      "account_id|account>accounts",
      "tags>tags*",
    ]);
    expect(formulaLinks(model, "accounts").map((l) => `${l.names.join("|")}>${l.target}${l.toMany ? "*" : ""}`)).toEqual([
      "account_transactions|transactions>transactions*",
    ]);
  });

  it("offers display names after [", () => {
    const result = complete("= [Boo");
    expect(result?.from).toBe(2);
    expect(result!.options.map((option) => option.label)).toContain("[Booked on]");
  });

  it("stays quiet inside text", () => {
    expect(complete('= IF(amount < 0, "exp')).toBeNull();
  });
});

describe("formula highlighting", () => {
  // Typing a quote used to throw inside the tokenizer and drop the keystroke.
  it.each([
    'IF(amount < 0, "expense", "income")',
    "'it''s' & \"say \"\"hi\"\"\"",
    '"unclosed',
    "[Booked on] > \"2026-01-01\"",
  ])("tokenizes %s", (doc) => {
    const state = EditorState.create({ doc, extensions: [formulaLanguage] });
    expect(() => ensureSyntaxTree(state, state.doc.length, 5000)).not.toThrow();
  });

  it("shows a formula with text in quotes", () => {
    render(
      <FormulaEditor
        value={'IF(amount < 0, "expense", "income")'}
        onChange={() => undefined}
        columns={columns}
        functions={functions}
      />,
    );
    expect(screen.getByTestId("formula-editor").textContent).toBe('IF(amount < 0, "expense", "income")');
  });
});

describe("FormulaField", () => {
  beforeEach(() => {
    api.loadFormulaFunctions.mockResolvedValue(functions);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("checks against the drafted columns, names the type and previews values", async () => {
    api.validateFormula.mockResolvedValue({ valid: true, type: "currency", errors: [], columns: ["amount"] });
    api.previewFormula.mockResolvedValue({
      type: "currency",
      columns: ["amount"],
      rows: [
        { id: 1, value: 45.9, inputs: { amount: -45.9 } },
        { id: 2, value: null, inputs: { amount: null } },
      ],
    });

    render(<FormulaField table="transactions" column="spent" model={model} value="= amount * -1" onChange={() => undefined} />);

    expect(await screen.findByText("Gives an amount · uses amount")).toBeTruthy();
    expect(api.validateFormula).toHaveBeenCalledWith({ table: "transactions", formula: "= amount * -1", column: "spent", model });
    expect(api.previewFormula).toHaveBeenCalledWith({ table: "transactions", formula: "= amount * -1", column: "spent", limit: 5 });
    const table = await screen.findByRole("table");
    expect(table.textContent).toContain("Result");
    expect(table.textContent).toMatch(/45[.,]90/);
    expect(table.textContent).toContain("—");
  });

  it("lists the errors and skips the preview", async () => {
    api.validateFormula.mockResolvedValue({
      valid: false,
      type: null,
      errors: [{ message: "Column 'amout' not found in transactions — did you mean 'amount'?", start: 2, end: 7 }],
      columns: [],
    });

    render(<FormulaField table="transactions" model={model} value="= amout * -1" onChange={() => undefined} />);

    expect(await screen.findByRole("alert")).toHaveProperty(
      "textContent",
      "Column 'amout' not found in transactions — did you mean 'amount'?",
    );
    expect(api.previewFormula).not.toHaveBeenCalled();
  });

  it("explains when values cannot be previewed before the draft is applied", async () => {
    api.validateFormula.mockResolvedValue({ valid: true, type: "currency", errors: [], columns: ["planned"] });
    api.previewFormula.mockRejectedValue(new Error("Table 'budgets' does not exist."));

    render(
      <FormulaField
        table="budgets"
        model={{ tables: [{ name: "budgets", columns: [{ name: "planned", type: "currency" }] }], relations: [] }}
        value="planned * 2"
        onChange={() => undefined}
      />,
    );

    expect(await screen.findByText("Apply the draft to see values from your data.")).toBeTruthy();
  });

  it("does not check an empty formula", async () => {
    render(<FormulaField table="transactions" model={model} value="" onChange={() => undefined} />);
    await waitFor(() => expect(api.loadFormulaFunctions).toHaveBeenCalled());
    expect(screen.getByText(/Start typing for suggestions/)).toBeTruthy();
    expect(api.validateFormula).not.toHaveBeenCalled();
  });
});

describe("computed values in the explorer", () => {
  it("look like their result type", () => {
    expect(valueTypeOf({ type: "computed", resultType: "currency" })).toBe("currency");
    expect(valueTypeOf({ type: "computed" })).toBe("text");
    expect(valueTypeOf({ type: "date" })).toBe("date");

    const { container } = render(<>{renderCellValue({ name: "balance", type: "computed", resultType: "currency" }, -1150)}</>);
    expect(container.textContent).toMatch(/1[.,]150[.,]00/);
    expect(container.textContent).toContain("€");
    expect(container.querySelector(".data-grid__amount--negative")).toBeTruthy();

    cleanup();
    const flag = render(<>{renderCellValue({ name: "paid", type: "computed", resultType: "boolean" }, true)}</>);
    expect(flag.container.textContent).toBe("Yes");
  });
});
