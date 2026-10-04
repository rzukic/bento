import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  analyzeImport,
  readFileAsBase64,
  runImport,
  type ImportAnalysis,
  type ImportFileSettings,
} from "../../../api/import";
import { ApiClientError } from "../../../api/client";
import { applySchemaDraft, getCanonicalSchemaSnapshot, previewSchemaDraft } from "../../../api/schema";
import { Icon } from "../../../components/common/Icon";
import { Alert } from "../../../components/ui/Alert";
import { Button } from "../../../components/ui/Button";
import { Spinner } from "../../../components/ui/Spinner";
import { TextField } from "../../../components/ui/TextField";
import { useExplorerStore } from "../../../stores/explorerStore";
import { useSchemaStore } from "../../../stores/schemaStore";
import type { ModelTable, SchemaColumnType } from "../../../types/api";
import type { ImportHandOff } from "./ImportPage";
import { FileDrop, ReadSettings } from "./ImportSections";
import {
  PLAIN_COLUMN_TYPES,
  buildTable,
  canBeLookup,
  proposeTable,
  toColumnName,
  validatePlan,
  withStoreAs,
  type ColumnPlan,
  type StoreAs,
  type TablePlan,
} from "./tableFromCsv";
import "./ImportPage.css";

const TYPE_LABELS: Record<string, string> = {
  text: "Text",
  integer: "Whole number",
  float: "Number",
  currency: "Amount",
  date: "Date",
  datetime: "Date and time",
  boolean: "Yes / no",
};

function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof ApiClientError) {
    if (cause.status === 403) return "Creating tables needs the admin role.";
    return cause.message;
  }
  return fallback;
}

function storeAsValue(storeAs: StoreAs): string {
  return storeAs.kind === "value" ? "value" : storeAs.kind === "new-table" ? "new-table" : `table:${storeAs.table}`;
}

export function NewTableFromCsvPage() {
  const navigate = useNavigate();
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  useEffect(() => {
    void loadSchema();
  }, [loadSchema]);

  const existingTables = useMemo<ModelTable[]>(() => snapshot?.tables ?? [], [snapshot]);
  const existingNames = useMemo(() => existingTables.map((t) => t.name), [existingTables]);
  // Junction tables only link records; they make poor lookup targets.
  const linkTargets = useMemo(() => existingTables.filter((t) => !t.name.startsWith("_jt_")), [existingTables]);

  const [file, setFile] = useState<File | null>(null);
  const [content, setContent] = useState<string | null>(null);
  const [analysis, setAnalysis] = useState<ImportAnalysis | null>(null);
  const [settings, setSettings] = useState<ImportFileSettings>({});
  const [plan, setPlan] = useState<TablePlan | null>(null);
  const [busy, setBusy] = useState<"analyze" | "create" | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  const read = async (fileContent: string, nextSettings: ImportFileSettings, fileName: string, keepNames: TablePlan | null) => {
    setBusy("analyze");
    setError(null);
    try {
      const next = await analyzeImport(fileContent, nextSettings, fileName);
      setAnalysis(next);
      setSettings(next.settings);
      const proposal = proposeTable(next, fileName, existingNames);
      setPlan(keepNames ? { ...proposal, name: keepNames.name, displayName: keepNames.displayName } : proposal);
    } catch (cause) {
      setAnalysis(null);
      setPlan(null);
      setError(errorMessage(cause, "The file could not be read."));
    } finally {
      setBusy(null);
    }
  };

  const chooseFile = async (next: File) => {
    setFile(next);
    try {
      const base64 = await readFileAsBase64(next);
      setContent(base64);
      await read(base64, {}, next.name, null);
    } catch {
      setError("The file could not be read.");
    }
  };

  const updateColumn = (index: number, change: (column: ColumnPlan) => ColumnPlan) =>
    setPlan((current) =>
      current ? { ...current, columns: current.columns.map((c) => (c.index === index ? change(c) : c)) } : current,
    );

  const problems = plan ? validatePlan(plan, existingNames) : [];

  const create = async () => {
    if (!plan || !content || !analysis || problems.length > 0) return;
    setBusy("create");
    setError(null);
    try {
      setProgress("Checking the new tables");
      const current = await getCanonicalSchemaSnapshot();
      const built = buildTable(current, plan, existingTables);
      const preview = await previewSchemaDraft(built.model);
      if (!preview.isValid) {
        setError(preview.errors.map((issue) => issue.message).join(" "));
        return;
      }

      setProgress("Creating the tables");
      await applySchemaDraft(built.model);
      await loadSchema({ force: true });

      const filled: string[] = [];
      for (const lookup of built.lookups) {
        setProgress(`Filling ${lookup.displayName}`);
        const result = await runImport(lookup.table, {
          content,
          fileName: file?.name,
          settings,
          mapping: [{ source: lookup.header, target: lookup.labelColumn }],
          keyColumns: [lookup.labelColumn],
          distinctKeys: true,
        });
        if (result.inserted > 0) useExplorerStore.getState().markTableStale(lookup.table);
        filled.push(`${result.inserted} ${result.inserted === 1 ? "value" : "values"} added to ${lookup.displayName}`);
      }

      const tableLabel = plan.displayName.trim() || plan.name;
      const handOff: ImportHandOff = {
        content,
        fileName: file?.name ?? "import.csv",
        fileType: file?.type ?? "text/csv",
        settings,
        mapping: built.mapping,
        keyColumns: built.keyColumns,
        notice: [`Created ${tableLabel}.`, ...filled.map((f) => `${f.charAt(0).toUpperCase()}${f.slice(1)}.`), "Check the rows below and import them."].join(" "),
      };
      navigate(`/data/${encodeURIComponent(plan.name.trim())}/import`, { state: { importHandOff: handOff } });
    } catch (cause) {
      setError(errorMessage(cause, "The table could not be created."));
    } finally {
      setBusy(null);
      setProgress(null);
    }
  };

  const columnsByIndex = useMemo(() => new Map((analysis?.columns ?? []).map((c) => [c.index, c])), [analysis]);

  return (
    <div className="import-page">
      <header className="import-page__header">
        <Link className="import-page__back" to="/data">
          <Icon name="chevron-left" size={14} />
          Data Explorer
        </Link>
        <h1 className="import-page__title">New table from CSV</h1>
        <p className="import-page__lead">
          Bento proposes a table from the file's columns: names, types, required columns and lookup tables for categories. Adjust it, create it, and the import continues right away.
        </p>
      </header>

      {error ? <Alert tone="error" message={error} /> : null}

      <section className="import-section" aria-labelledby="new-file-heading">
        <div className="import-section__head">
          <span className="import-section__step">1</span>
          <h2 id="new-file-heading" className="import-section__title">File</h2>
        </div>
        <FileDrop file={file} rowCount={analysis?.rowCount ?? null} inputRef={fileInputRef} onChoose={(chosen) => void chooseFile(chosen)} />
        {busy === "analyze" ? <Spinner label="Reading the file" /> : null}
      </section>

      {analysis && plan ? (
        <>
          <section className="import-section" aria-labelledby="new-read-heading">
            <div className="import-section__head">
              <span className="import-section__step">2</span>
              <h2 id="new-read-heading" className="import-section__title">How the file is read</h2>
              <span className="import-section__note">Detected; change anything that looks wrong.</span>
            </div>
            <ReadSettings
              analysis={analysis}
              settings={settings}
              onChange={(patch) => content && void read(content, { ...settings, ...patch }, file?.name ?? "import.csv", plan)}
            />
          </section>

          <section className="import-section" aria-labelledby="new-table-heading">
            <div className="import-section__head">
              <span className="import-section__step">3</span>
              <h2 id="new-table-heading" className="import-section__title">Table</h2>
              <span className="import-section__note">Every table also gets an <code>id</code> column.</span>
            </div>
            <div className="import-plan__names">
              <TextField
                label="Name"
                value={plan.name}
                onChange={(event) => setPlan({ ...plan, name: event.target.value })}
                hint="Used in the API"
              />
              <TextField
                label="Display name"
                value={plan.displayName}
                onChange={(event) => setPlan({ ...plan, displayName: event.target.value })}
              />
            </div>
            <table className="import-map import-plan">
              <thead>
                <tr>
                  <th scope="col" className="import-map__key-head">Use</th>
                  <th scope="col">File column</th>
                  <th scope="col">Column name</th>
                  <th scope="col">Stored as</th>
                  <th scope="col" className="import-map__key-head">Required</th>
                </tr>
              </thead>
              <tbody>
                {plan.columns.map((column) => {
                  const analyzed = columnsByIndex.get(column.index);
                  const lookupPossible = analyzed ? canBeLookup(analyzed, analysis.rowCount) : false;
                  const isLink = column.storeAs.kind !== "value";
                  const fallbackType = (analyzed && PLAIN_COLUMN_TYPES.includes(analyzed.inferredType as SchemaColumnType)
                    ? analyzed.inferredType
                    : "text") as SchemaColumnType;
                  return (
                    <tr key={column.index} className={column.include ? "import-map__row import-map__row--mapped" : "import-map__row"}>
                      <td className="import-map__key">
                        <input
                          type="checkbox"
                          aria-label={`Use ${column.header}`}
                          checked={column.include}
                          onChange={(event) => updateColumn(column.index, (c) => ({ ...c, include: event.target.checked }))}
                        />
                      </td>
                      <th scope="row" className="import-map__column">
                        <span className="import-map__name">{column.header}</span>
                        <span className="import-map__type">
                          {analyzed?.samples.slice(0, 2).join(" · ") || "empty"}
                          {lookupPossible ? ` · ${analyzed!.distinctCount} different` : ""}
                        </span>
                      </th>
                      <td>
                        <input
                          className="field__control field__control--compact import-plan__input"
                          aria-label={`Column name for ${column.header}`}
                          value={column.name}
                          disabled={!column.include}
                          onChange={(event) => updateColumn(column.index, (c) => ({ ...c, name: event.target.value }))}
                          onBlur={(event) => updateColumn(column.index, (c) => ({ ...c, name: toColumnName(event.target.value) || c.name }))}
                        />
                      </td>
                      <td>
                        <div className="import-plan__store">
                          <select
                            className="field__control field__control--compact"
                            aria-label={`Stored as for ${column.header}`}
                            disabled={!column.include}
                            value={isLink ? storeAsValue(column.storeAs) : column.type}
                            onChange={(event) => {
                              const value = event.target.value;
                              updateColumn(column.index, (c) => {
                                if (value === "new-table") {
                                  const table = toColumnName(c.header);
                                  return withStoreAs(c, { kind: "new-table", table }, fallbackType);
                                }
                                if (value.startsWith("table:")) {
                                  return withStoreAs(c, { kind: "existing-table", table: value.slice(6) }, fallbackType);
                                }
                                const plain = withStoreAs(c, { kind: "value" }, fallbackType);
                                return { ...plain, type: value as SchemaColumnType };
                              });
                            }}
                          >
                            <optgroup label="Value">
                              {PLAIN_COLUMN_TYPES.map((type) => (
                                <option key={type} value={type}>
                                  {TYPE_LABELS[type] ?? type}
                                </option>
                              ))}
                            </optgroup>
                            {lookupPossible || isLink ? (
                              <optgroup label="Link">
                                <option value="new-table">New table of values</option>
                                {linkTargets.map((table) => (
                                  <option key={table.name} value={`table:${table.name}`}>
                                    Link to {table.displayName ?? table.name}
                                  </option>
                                ))}
                              </optgroup>
                            ) : null}
                          </select>
                          {column.storeAs.kind === "new-table" ? (
                            <input
                              className="field__control field__control--compact import-plan__input"
                              aria-label={`New table name for ${column.header}`}
                              value={column.storeAs.table}
                              disabled={!column.include}
                              onChange={(event) =>
                                updateColumn(column.index, (c) => ({ ...c, storeAs: { kind: "new-table", table: event.target.value } }))
                              }
                              onBlur={(event) =>
                                updateColumn(column.index, (c) => ({
                                  ...c,
                                  storeAs: { kind: "new-table", table: toColumnName(event.target.value) || event.target.value },
                                }))
                              }
                            />
                          ) : null}
                        </div>
                      </td>
                      <td className="import-map__key">
                        <input
                          type="checkbox"
                          aria-label={`${column.header} is required`}
                          checked={column.required}
                          disabled={!column.include || isLink}
                          onChange={(event) => updateColumn(column.index, (c) => ({ ...c, required: event.target.checked }))}
                        />
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {problems.length > 0 ? (
              <ul className="import-plan__problems">
                {problems.map((problem) => (
                  <li key={problem}>{problem}</li>
                ))}
              </ul>
            ) : null}
            <div className="import-actions">
              <Button type="button" variant="primary" onClick={() => void create()} disabled={problems.length > 0 || busy !== null}>
                {busy === "create" ? "Creating…" : "Create table and continue"}
              </Button>
              {progress ? <span className="import-section__note">{progress}…</span> : null}
            </div>
          </section>
        </>
      ) : null}
    </div>
  );
}
