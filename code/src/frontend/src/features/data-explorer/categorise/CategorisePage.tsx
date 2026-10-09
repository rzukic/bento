import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { getAiStatus } from "../../../api/ai";
import type { CategoriseRow } from "../../../api/categorise";
import { ApiClientError } from "../../../api/client";
import { fetchTableRows, patchRow } from "../../../api/data";
import { Icon } from "../../../components/common/Icon";
import { Alert } from "../../../components/ui/Alert";
import { Button } from "../../../components/ui/Button";
import { Spinner } from "../../../components/ui/Spinner";
import { useDocumentTitle } from "../../../hooks/useDocumentTitle";
import { useExplorerStore } from "../../../stores/explorerStore";
import { useSchemaStore } from "../../../stores/schemaStore";
import type { ModelColumn } from "../../../types/api";
import { CategoriseBar, CategoryCell } from "./CategoriseControls";
import { categorisableColumns, defaultCategoryColumn, useCategorise } from "./useCategorise";
import "../import/ImportPage.css";

const LOAD_ROWS = 200;
const SHOWN_TYPES = new Set(["text", "string", "currency", "float", "double", "integer", "int64", "date", "datetime", "enum"]);

function cell(value: unknown): string | null {
  if (value === null || value === undefined || value === "") return null;
  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

/**
 * Categorise existing rows (Step 4.8): the newest rows without a value in a
 * link or choice column, with suggestions from rules, earlier rows and the
 * AI, reviewed and saved in one go.
 */
export function CategorisePage() {
  const { tableId = "" } = useParams();
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  useEffect(() => {
    void loadSchema();
  }, [loadSchema]);

  const table = snapshot?.tables.find((t) => t.name === tableId) ?? null;
  const displayName = table?.displayName ?? table?.name ?? tableId;
  useDocumentTitle(`Categorise ${displayName} · Bento`);

  const columns = useMemo(() => categorisableColumns(table), [table]);
  const [chosenColumn, setChosenColumn] = useState<string | null>(null);
  const column = chosenColumn ?? (defaultCategoryColumn(table) || (columns[0]?.name ?? ""));
  const categorising = useCategorise(tableId, column);
  const primaryKey = table?.columns.find((c) => c.primaryKey)?.name ?? "id";
  const shown = useMemo<ModelColumn[]>(
    () => (table?.columns ?? []).filter((c) => !c.primaryKey && !c.formula && c.name !== column && SHOWN_TYPES.has(String(c.type))).slice(0, 5),
    [table, column],
  );

  const [rows, setRows] = useState<Record<string, unknown>[] | null>(null);
  const [total, setTotal] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);
  const [aiEnabled, setAiEnabled] = useState(false);

  useEffect(() => {
    getAiStatus()
      .then((status) => setAiEnabled(status.enabled && status.configured))
      .catch(() => setAiEnabled(false));
  }, []);

  const load = useCallback(async () => {
    if (!table || !column) return;
    setRows(null);
    setError(null);
    try {
      const response = await fetchTableRows(table.name, {
        page: 1,
        perPage: LOAD_ROWS,
        sort: [{ column: primaryKey, dir: "desc" }],
        search: "",
        filters: [{ column, op: "null" }],
      });
      setRows(response.data);
      setTotal(response.meta.total);
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The rows could not be loaded.");
      setRows([]);
    }
  }, [table, column, primaryKey]);

  useEffect(() => {
    void load();
  }, [load]);

  const categoriseRows = useMemo<CategoriseRow[]>(
    () =>
      (rows ?? []).map((row) => ({
        key: String(row[primaryKey]),
        values: Object.fromEntries(shown.map((c) => [c.name, cell(row[c.name])])),
      })),
    [rows, shown, primaryKey],
  );

  const start = categorising.start;
  useEffect(() => {
    if (categoriseRows.length > 0) void start(categoriseRows);
  }, [categoriseRows, start]);

  const columnDef = columns.find((c) => c.name === column) ?? null;
  const label = columnDef ? (columnDef.displayName ?? columnDef.name) : "";
  const chosen = Object.entries(categorising.choices);

  const save = async () => {
    if (!columnDef) return;
    setSaving(true);
    setError(null);
    setSaved(null);
    let written = 0;
    try {
      for (const [id, choice] of chosen) {
        const value = columnDef.type === "reference" && /^-?\d+$/.test(choice.value) ? Number(choice.value) : choice.value;
        await patchRow(tableId, id, { [column]: value });
        written += 1;
      }
      const rules = await categorising.saveRules();
      useExplorerStore.getState().markTableStale(tableId);
      setSaved(
        `${written} ${written === 1 ? "row" : "rows"} categorised.` +
          (rules.length > 0 ? ` Saved ${rules.length} ${rules.length === 1 ? "rule" : "rules"}: ${rules.map((r) => `“${r.pattern}”`).join(", ")}.` : ""),
      );
      await load();
    } catch (cause) {
      setError(
        `${written > 0 ? `${written} rows were saved, then: ` : ""}${cause instanceof ApiClientError ? cause.message : "The rows could not be saved."}`,
      );
    } finally {
      setSaving(false);
    }
  };

  if (!snapshot) {
    return (
      <div className="import-page">
        <Spinner label="Loading tables" />
      </div>
    );
  }
  if (!table) {
    return (
      <div className="import-page">
        <Alert tone="error" message={`There is no table "${tableId}".`} />
      </div>
    );
  }

  return (
    <div className="import-page">
      <header className="import-page__header">
        <Link className="import-page__back" to={`/data/${tableId}`}>
          <Icon name="chevron-left" size={14} />
          {displayName}
        </Link>
        <h1 className="import-page__title">Categorise {displayName}</h1>
        <p className="import-page__lead">
          Rows without a value, newest first. Rules and earlier rows fill in what they can; ✨ asks Bento's AI for the rest. Check, change what is wrong, then save.
        </p>
      </header>

      {error ? <Alert tone="error" message={error} /> : null}
      {saved ? <Alert tone="success" message={saved} /> : null}

      {columns.length === 0 ? (
        <Alert tone="info" message={`${displayName} has no link or choice column to categorise.`} />
      ) : (
        <section className="import-section" aria-label="Rows to categorise">
          <CategoriseBar
            columns={columns}
            column={column}
            onColumnChange={(next) => {
              setChosenColumn(next);
              setSaved(null);
            }}
            categorising={categorising}
            rows={categoriseRows}
            aiEnabled={aiEnabled}
          />

          {rows === null ? (
            <Spinner label="Loading rows" />
          ) : rows.length === 0 ? (
            <p className="import-section__note">Every row has a value in {label}.</p>
          ) : (
            <>
              {total > rows.length ? (
                <p className="import-section__note">
                  Showing the newest {rows.length} of {total} rows without a value; save them to see the next ones.
                </p>
              ) : null}
              <div className="import-review">
                <table className="import-review__table">
                  <thead>
                    <tr>
                      {shown.map((c) => (
                        <th key={c.name} scope="col">
                          {c.displayName ?? c.name}
                        </th>
                      ))}
                      <th scope="col">{label}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {categoriseRows.map((row) => (
                      <tr key={row.key} className="import-review__row">
                        {shown.map((c) => (
                          <td key={c.name} className="import-review__cell">
                            {row.values[c.name] ?? <span className="import-review__empty">—</span>}
                          </td>
                        ))}
                        <td className="import-review__cell">
                          <CategoryCell
                            rowKey={row.key}
                            label={label}
                            options={categorising.options}
                            choice={categorising.choices[row.key]}
                            categorising={categorising}
                          />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="import-actions">
                <Button type="button" variant="primary" disabled={saving || chosen.length === 0 || categorising.busy !== null} onClick={() => void save()}>
                  {saving ? "Saving…" : chosen.length === 0 ? "Nothing to save" : `Save ${chosen.length} ${chosen.length === 1 ? "row" : "rows"}`}
                </Button>
              </div>
            </>
          )}
        </section>
      )}
    </div>
  );
}
