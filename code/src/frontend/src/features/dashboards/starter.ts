import type { DashboardDocument, DashboardWidget, WidgetType } from "../../api/dashboards";
import type { AggregationQuery } from "../../api/query";
import type { ModelColumn, ModelTable } from "../../types/api";
import { addItem, type LayoutItem } from "./layout";

const DEFAULT_SIZE: Record<WidgetType, { w: number; h: number }> = {
  kpi: { w: 3, h: 2 },
  line: { w: 6, h: 4 },
  area: { w: 6, h: 4 },
  bar: { w: 6, h: 4 },
  donut: { w: 4, h: 4 },
  sankey: { w: 8, h: 5 },
  table: { w: 6, h: 4 },
  text: { w: 4, h: 2 },
};

export const WIDGET_LABELS: Record<WidgetType, string> = {
  kpi: "Key figure",
  line: "Line chart",
  area: "Area chart",
  bar: "Bar chart",
  donut: "Donut",
  sankey: "Sankey",
  table: "Table",
  text: "Text",
};

/** Tables that hold records (not N:M link tables). */
export function dataTables(tables: ModelTable[]): ModelTable[] {
  return tables.filter((t) => !t.name.startsWith("_jt_"));
}

const usable = (column: ModelColumn) => !column.primaryKey && column.type !== "computed";

const PAYEE = /counterparty|payee|recipient|merchant|auftraggeber|empf|beg(ue|ü)nstig|zahlungs|name/i;

export function pickColumns(table: ModelTable) {
  const columns = table.columns.filter(usable);
  const texts = columns.filter((c) => c.type === "text");
  return {
    date: columns.find((c) => c.type === "date" || c.type === "datetime"),
    amount:
      columns.find((c) => c.type === "currency") ??
      columns.find((c) => c.type === "float") ??
      columns.find((c) => c.type === "integer"),
    category: columns.find((c) => c.type === "reference" || c.type === "enum"),
    // The payee reads best in a "top" list; otherwise the first text column.
    text: texts.find((c) => PAYEE.test(`${c.name} ${c.displayName ?? ""}`)) ?? texts[0],
  };
}

export type CashFlowColumns = {
  date: ModelColumn;
  amount: ModelColumn;
  category?: ModelColumn;
  payee?: ModelColumn;
};

/**
 * What a cash-flow dashboard is built from: a date and a real amount (a
 * currency column — plain numbers are counts or quantities as often as
 * money), plus a category and a payee column when the table has them.
 */
export function cashFlowColumns(table: ModelTable): CashFlowColumns | null {
  const { date, category, text } = pickColumns(table);
  const amount = table.columns.filter(usable).find((c) => c.type === "currency");
  if (!date || !amount) return null;
  return { date, amount, category, payee: text };
}

/** A table a cash-flow dashboard can be made from. */
export function isFinanceTable(table: ModelTable): boolean {
  return cashFlowColumns(table) !== null;
}

const label = (column: ModelColumn) => column.displayName ?? column.name;

/** What the cash-flow dashboard of this table will show, in its own column names. */
export function describeCashFlow(table: ModelTable): string {
  const columns = cashFlowColumns(table);
  if (!columns) return "";
  const parts = [`Balance and net per month from ${label(columns.amount)} by ${label(columns.date)}`];
  if (columns.category) parts.push(`spending by ${label(columns.category)}`);
  if (columns.payee) parts.push(`top ${label(columns.payee)}`);
  return parts.length === 1 ? `${parts[0]}.` : `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}.`;
}

let counter = 0;
export function newWidgetId(): string {
  counter += 1;
  return `w${Date.now().toString(36)}${counter.toString(36)}`;
}

/** A sensible first query for a new widget of this type on this table. */
export function defaultQuery(type: WidgetType, table: ModelTable): AggregationQuery | undefined {
  if (type === "text") return undefined;
  const { date, amount, category, text } = pickColumns(table);
  const value = amount ? { name: amount.name, fn: "sum" as const, column: amount.name } : { name: "rows", fn: "count" as const };
  switch (type) {
    case "kpi":
      return { source: table.name, measures: [value] };
    case "donut": {
      const by = category ?? text;
      return by
        ? { source: table.name, groupBy: [{ column: by.name }], measures: [value], sort: [`-${value.name}`], limit: 12 }
        : { source: table.name, measures: [value] };
    }
    case "sankey": {
      // Income categories → income → expense categories: one category and the amount.
      const by = category ?? text;
      return by ? { source: table.name, groupBy: [{ column: by.name, top: 8, other: true, rank: "abs" }], measures: [value] } : { source: table.name, measures: [value] };
    }
    case "table": {
      const by = text ?? category;
      return by
        ? { source: table.name, groupBy: [{ column: by.name }], measures: [value, { name: "rows", fn: "count" }], sort: [`-${value.name}`], limit: 10 }
        : { source: table.name, measures: [value] };
    }
    default:
      return date
        ? { source: table.name, groupBy: [{ column: date.name, bucket: "month", as: "month" }], measures: [value] }
        : { source: table.name, measures: [value] };
  }
}

export function newWidget(type: WidgetType, table: ModelTable | undefined, layout: LayoutItem[]): DashboardWidget {
  const id = newWidgetId();
  const size = DEFAULT_SIZE[type];
  const placed = addItem(layout, id, size.w, size.h).find((item) => item.id === id)!;
  return {
    id,
    type,
    title: type === "text" ? "" : WIDGET_LABELS[type],
    position: { x: placed.x, y: placed.y, w: placed.w, h: placed.h },
    data: type === "text" || !table ? undefined : { query: defaultQuery(type, table) },
    display: type === "text" ? { text: "# Notes\nWhat this dashboard is for." } : { format: "auto" },
  };
}

/**
 * "Monthly Cash Flow" for a table with a date and an amount: balance and
 * net KPIs, the balance over time (running total that starts from the rows
 * before the range), net per month, spending by category, top
 * counterparties and the money flow (income → expenses by category).
 */
export function cashFlowDashboard(table: ModelTable): DashboardDocument {
  const columns = cashFlowColumns(table);
  if (!columns) return { dateRange: { preset: "last-12-months" }, widgets: [] };
  const { date, amount, category, payee: text } = columns;
  const month = { column: date.name, bucket: "month" as const, as: "month" };
  const sum = (name: string) => ({ name, fn: "sum" as const, column: amount.name });
  const widgets: DashboardWidget[] = [
    {
      id: "balance",
      type: "kpi",
      title: "Balance",
      position: { x: 0, y: 0, w: 4, h: 2 },
      data: { query: { source: table.name, groupBy: [month], measures: [{ ...sum("balance"), running: true, carryIn: true }] } },
      display: { format: "auto", compare: true },
    },
    {
      id: "net",
      type: "kpi",
      title: "Net per month",
      position: { x: 4, y: 0, w: 4, h: 2 },
      data: { query: { source: table.name, groupBy: [month], measures: [sum("net")] } },
      display: { format: "auto", compare: true },
    },
    {
      id: "spent",
      type: "kpi",
      title: "Spent in the range",
      position: { x: 8, y: 0, w: 4, h: 2 },
      data: { query: { source: table.name, filter: { [amount.name]: { lt: 0 } }, measures: [sum("spent")] }, dateColumn: date.name },
      display: { format: "auto" },
    },
    {
      id: "balance-over-time",
      type: "area",
      title: "Balance over time",
      position: { x: 0, y: 2, w: 8, h: 4 },
      data: { query: { source: table.name, groupBy: [month], measures: [{ ...sum("balance"), running: true, carryIn: true }] } },
      display: { format: "auto" },
    },
    {
      id: "net-per-month",
      type: "bar",
      title: "Net per month",
      position: { x: 8, y: 2, w: 4, h: 4 },
      data: { query: { source: table.name, groupBy: [month], measures: [sum("net")] } },
      display: { format: "auto" },
    },
  ];
  if (category) {
    widgets.push({
      id: "spending-by-category",
      type: "donut",
      title: `Spending by ${category.displayName ?? category.name}`,
      position: { x: 0, y: 6, w: 5, h: 4 },
      data: {
        query: { source: table.name, filter: { [amount.name]: { lt: 0 } }, groupBy: [{ column: category.name }], measures: [sum("spent")], sort: ["spent"] },
        dateColumn: date.name,
      },
      display: { format: "auto" },
    });
  }
  if (text) {
    widgets.push({
      id: "top-counterparties",
      type: "table",
      title: `Top spending: ${text.displayName ?? text.name}`,
      position: { x: category ? 5 : 0, y: 6, w: category ? 7 : 12, h: 4 },
      data: {
        query: {
          source: table.name,
          filter: { [amount.name]: { lt: 0 } },
          groupBy: [{ column: text.name }],
          measures: [sum("spent"), { name: "bookings", fn: "count" }],
          sort: ["spent"],
          limit: 10,
        },
        dateColumn: date.name,
      },
      display: { format: "auto" },
    });
  }
  if (category) {
    // Where the money goes: income by category → expenses by category, the rest saved.
    widgets.push({
      id: "money-flow",
      type: "sankey",
      title: "Money flow",
      position: { x: 0, y: 10, w: 12, h: 5 },
      data: { query: { source: table.name, groupBy: [{ column: category.name, top: 8, other: true, rank: "abs" }], measures: [sum("amount")] }, dateColumn: date.name },
      display: { format: "auto", sankey: "cashflow" },
    });
  }
  return { dateRange: { preset: "last-12-months" }, refreshSeconds: 0, widgets };
}
