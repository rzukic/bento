import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useLocation, useNavigate, useParams } from "react-router-dom";
import {
  analyzeImport,
  createImportProfile,
  deleteImportProfile,
  listImportProfiles,
  previewImport,
  readFileAsBase64,
  runImport,
  updateImportProfile,
  type ImportAnalysis,
  type ImportColumnMapping,
  type ImportFileSettings,
  type ImportProfile,
  type ImportProfileConfig,
  type ImportRowStatus,
  type ImportRunPayload,
  type ImportRunResult,
} from "../../../api/import";
import { ApiClientError } from "../../../api/client";
import { Icon } from "../../../components/common/Icon";
import { Alert } from "../../../components/ui/Alert";
import { Button } from "../../../components/ui/Button";
import { SelectField } from "../../../components/ui/SelectField";
import { Spinner } from "../../../components/ui/Spinner";
import { TextField } from "../../../components/ui/TextField";
import { useExplorerStore } from "../../../stores/explorerStore";
import { useSchemaStore } from "../../../stores/schemaStore";
import type { ModelColumn } from "../../../types/api";
import { fetchTableRows } from "../../../api/data";
import { getDisplayColumnForTable } from "../components/editors/referenceMetadata";
import {
  applyProfile,
  getImportableColumns,
  isRequiredColumn,
  retainMapping,
  FIXED,
  asFixed,
  fixedValue,
  isFixed,
  suggestKeyColumns,
  suggestMapping,
  toMappingPayload,
  type MappingState,
} from "./importMapping";
import { FileDrop, ReadSettings } from "./ImportSections";
import { getAiStatus } from "../../../api/ai";
import type { CategoriseRow } from "../../../api/categorise";
import { CategoriseBar, CategoryCell } from "../categorise/CategoriseControls";
import { categorisableColumns, defaultCategoryColumn, useCategorise } from "../categorise/useCategorise";
import "./ImportPage.css";

/**
 * Router state from "New table from CSV": the file and the mapping into the
 * table just created, so the import continues without choosing anything again.
 */
export type ImportHandOff = {
  content: string;
  fileName: string;
  fileType: string;
  settings: ImportFileSettings;
  mapping: ImportColumnMapping[];
  keyColumns: string[];
  notice: string;
};

type ReviewFilter = "all" | ImportRowStatus;

const REVIEW_ROW_LIMIT = 200;

function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof ApiClientError) {
    if (cause.status === 403) return "Importing needs the editor role.";
    return cause.message;
  }
  return fallback;
}

function countRows(count: number): string {
  return `${count} ${count === 1 ? "row" : "rows"}`;
}

function skippedSummary(result: ImportRunResult): string {
  const parts = [
    result.duplicates > 0 ? `${countRows(result.duplicates)} already in the table` : null,
    result.invalid > 0 ? `${countRows(result.invalid)} with problems` : null,
  ].filter(Boolean);
  return parts.length === 0
    ? "Every row of the file is in the table now."
    : `Skipped ${parts.join(" and ")}.`;
}

export function ImportPage() {
  const { tableId = "" } = useParams();
  const navigate = useNavigate();
  const snapshot = useSchemaStore((state) => state.snapshot);
  const schemaStatus = useSchemaStore((state) => state.status);
  const loadSchema = useSchemaStore((state) => state.load);

  useEffect(() => {
    void loadSchema();
  }, [loadSchema]);

  const table = snapshot?.tables.find((t) => t.name === tableId) ?? null;
  const displayName = table?.displayName ?? table?.name ?? tableId;
  const columns = useMemo(() => (table ? getImportableColumns(table) : []), [table]);

  const [file, setFile] = useState<File | null>(null);
  const [content, setContent] = useState<string | null>(null);
  const [analysis, setAnalysis] = useState<ImportAnalysis | null>(null);
  const [settings, setSettings] = useState<ImportFileSettings>({});
  const [mapping, setMapping] = useState<MappingState>({});
  const [keyColumns, setKeyColumns] = useState<string[]>([]);
  const [profiles, setProfiles] = useState<ImportProfile[]>([]);
  const [profileId, setProfileId] = useState<number | null>(null);
  const [profileName, setProfileName] = useState("");
  const [profileNotice, setProfileNotice] = useState<string | null>(null);
  const [preview, setPreview] = useState<ImportRunResult | null>(null);
  const [result, setResult] = useState<ImportRunResult | null>(null);
  const [reviewFilter, setReviewFilter] = useState<ReviewFilter>("all");
  const [busy, setBusy] = useState<"analyze" | "preview" | "import" | "profile" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const categoryColumns = useMemo(() => categorisableColumns(table), [table]);
  const [categoryColumn, setCategoryColumn] = useState<string | null>(null);
  const activeCategoryColumn = categoryColumn ?? defaultCategoryColumn(table);
  const categorising = useCategorise(tableId, activeCategoryColumn);
  const [aiEnabled, setAiEnabled] = useState(false);
  const [rulesNotice, setRulesNotice] = useState<string | null>(null);
  useEffect(() => {
    getAiStatus()
      .then((status) => setAiEnabled(status.enabled && status.configured))
      .catch(() => setAiEnabled(false));
  }, []);
  const mappingRef = useRef(mapping);
  const keyColumnsRef = useRef(keyColumns);
  mappingRef.current = mapping;
  keyColumnsRef.current = keyColumns;

  const selectedProfile = profiles.find((p) => p.id === profileId) ?? null;
  const headers = useMemo(() => analysis?.columns.map((c) => c.name) ?? [], [analysis]);

  useEffect(() => {
    if (!tableId) return;
    listImportProfiles(tableId)
      .then(setProfiles)
      .catch(() => setProfiles([]));
  }, [tableId]);

  // Any change to how the file is read or mapped makes an earlier check stale.
  const invalidate = useCallback(() => {
    setPreview(null);
    setResult(null);
  }, []);

  const analyze = useCallback(
    async (fileContent: string, nextSettings: ImportFileSettings, preset: ImportProfileConfig | null, fileName?: string): Promise<boolean> => {
      setBusy("analyze");
      setError(null);
      invalidate();
      try {
        const next = await analyzeImport(fileContent, nextSettings, fileName);
        const nextHeaders = next.columns.map((c) => c.name);
        setAnalysis(next);
        setSettings(next.settings);
        if (preset) {
          const applied = applyProfile(preset, nextHeaders, columns);
          setMapping(applied.mapping);
          setKeyColumns(applied.keyColumns);
        } else {
          // Re-reading the same file keeps the user's choices where the
          // headers still exist; a new file gets a fresh suggestion.
          const kept = retainMapping(mappingRef.current, nextHeaders);
          const nextMapping = Object.values(kept).some(Boolean) ? kept : suggestMapping(nextHeaders, columns);
          const keptKeys = keyColumnsRef.current.filter((k) => nextMapping[k]);
          setMapping(nextMapping);
          setKeyColumns(keptKeys.length > 0 ? keptKeys : suggestKeyColumns(nextMapping, columns));
        }
        return true;
      } catch (cause) {
        setAnalysis(null);
        setError(errorMessage(cause, "The file could not be read."));
        return false;
      } finally {
        setBusy(null);
      }
    },
    [columns, invalidate],
  );

  const chooseFile = useCallback(
    async (next: File) => {
      setFile(next);
      // A new file starts from a fresh suggestion, not the previous mapping.
      mappingRef.current = {};
      keyColumnsRef.current = [];
      setMapping({});
      setKeyColumns([]);
      try {
        const base64 = await readFileAsBase64(next);
        setContent(base64);
        await analyze(base64, selectedProfile?.config.settings ?? {}, selectedProfile?.config ?? null, next.name);
      } catch {
        setError("The file could not be read.");
      }
    },
    [analyze, selectedProfile],
  );

  // Coming from "New table from CSV": read the same file with the same
  // settings and mapping, then check the rows right away.
  const location = useLocation();
  const handOff = (location.state as { importHandOff?: ImportHandOff } | null)?.importHandOff ?? null;
  const handOffStarted = useRef(false);
  const [autoCheck, setAutoCheck] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  useEffect(() => {
    if (!handOff || handOffStarted.current || columns.length === 0) return;
    handOffStarted.current = true;
    const bytes = Uint8Array.from(atob(handOff.content), (c) => c.charCodeAt(0));
    setFile(new File([bytes], handOff.fileName, { type: handOff.fileType || "text/csv" }));
    setContent(handOff.content);
    setNotice(handOff.notice);
    void analyze(handOff.content, handOff.settings, { mapping: handOff.mapping, keyColumns: handOff.keyColumns }, handOff.fileName).then(
      (ok) => setAutoCheck(ok),
    );
  }, [handOff, columns, analyze]);

  const changeSetting = (patch: ImportFileSettings) => {
    if (!content) return;
    void analyze(content, { ...settings, ...patch }, null, file?.name);
  };

  const chooseProfile = (value: string) => {
    const id = value ? Number(value) : null;
    setProfileId(id);
    setProfileNotice(null);
    const profile = profiles.find((p) => p.id === id) ?? null;
    setProfileName(profile?.name ?? "");
    if (content && profile) void analyze(content, profile.config.settings ?? {}, profile.config, file?.name);
  };

  // New rows without a value in the column to categorise: these get suggestions.
  const categoryRows = useMemo<CategoriseRow[]>(() => {
    if (!preview || result || !activeCategoryColumn) return [];
    return preview.rows
      .filter((row) => row.status === "new" && !row.display[activeCategoryColumn])
      .map((row) => ({
        key: String(row.line),
        values: Object.fromEntries(Object.entries(row.display).filter(([name]) => name !== activeCategoryColumn)),
      }));
  }, [preview, result, activeCategoryColumn]);

  const startCategorising = categorising.start;
  useEffect(() => {
    if (categoryRows.length > 0) void startCategorising(categoryRows);
  }, [categoryRows, startCategorising]);

  const payload = (): ImportRunPayload | null =>
    content
      ? {
          content,
          fileName: file?.name,
          settings,
          mapping: toMappingPayload(mapping),
          keyColumns: keyColumns.filter((k) => mapping[k]),
          ...(activeCategoryColumn && Object.keys(categorising.choices).length > 0
            ? {
                assign: {
                  column: activeCategoryColumn,
                  values: Object.fromEntries(Object.entries(categorising.choices).map(([line, choice]) => [line, choice.value])),
                },
              }
            : {}),
        }
      : null;

  const runCheck = async () => {
    const body = payload();
    if (!body) return;
    setBusy("preview");
    setError(null);
    setResult(null);
    try {
      setPreview(await previewImport(tableId, body));
      setReviewFilter("all");
    } catch (cause) {
      setError(errorMessage(cause, "The rows could not be checked."));
    } finally {
      setBusy(null);
    }
  };

  const runCheckRef = useRef(runCheck);
  runCheckRef.current = runCheck;
  useEffect(() => {
    if (!autoCheck || !analysis || !content) return;
    setAutoCheck(false);
    void runCheckRef.current();
  }, [autoCheck, analysis, content]);

  const runImportNow = async () => {
    const body = payload();
    if (!body) return;
    setBusy("import");
    setError(null);
    try {
      const done = await runImport(tableId, body);
      if (done.inserted > 0) useExplorerStore.getState().markTableStale(tableId);
      setResult(done);
      setPreview(done);
      // Corrections become rules for the next import ("REWE" → Groceries).
      try {
        const imported = new Set(done.rows.filter((r) => r.status === "new").map((r) => String(r.line)));
        const saved = await categorising.saveRules([...imported]);
        setRulesNotice(
          saved.length > 0
            ? `Saved ${saved.length} ${saved.length === 1 ? "rule" : "rules"} for the next import: ${saved.map((r) => `“${r.pattern}”`).join(", ")}.`
            : null,
        );
      } catch {
        setRulesNotice("The rows are imported, but your corrections could not be saved as rules.");
      }
    } catch (cause) {
      setError(errorMessage(cause, "The import failed; no rows were written."));
    } finally {
      setBusy(null);
    }
  };

  const saveProfile = async (asNew: boolean) => {
    const name = profileName.trim();
    if (!name) {
      setProfileNotice("Give the profile a name.");
      return;
    }
    const config = {
      settings: { encoding: settings.encoding, delimiter: settings.delimiter, decimalSeparator: settings.decimalSeparator, dateFormat: settings.dateFormat },
      mapping: toMappingPayload(mapping),
      keyColumns: keyColumns.filter((k) => mapping[k]),
    };
    setBusy("profile");
    setProfileNotice(null);
    try {
      const saved = selectedProfile && !asNew
        ? await updateImportProfile(tableId, selectedProfile.id, name, config)
        : await createImportProfile(tableId, name, config);
      const refreshed = await listImportProfiles(tableId);
      setProfiles(refreshed);
      setProfileId(saved.id);
      setProfileNotice(`Saved "${saved.name}".`);
    } catch (cause) {
      setProfileNotice(errorMessage(cause, "The profile could not be saved."));
    } finally {
      setBusy(null);
    }
  };

  const removeProfile = async () => {
    if (!selectedProfile) return;
    setBusy("profile");
    try {
      await deleteImportProfile(tableId, selectedProfile.id);
      setProfiles((current) => current.filter((p) => p.id !== selectedProfile.id));
      setProfileId(null);
      setProfileName("");
      setProfileNotice(`Deleted "${selectedProfile.name}".`);
    } catch (cause) {
      setProfileNotice(errorMessage(cause, "The profile could not be deleted."));
    } finally {
      setBusy(null);
    }
  };

  const mappedColumns = columns.filter((c) => mapping[c.name]);
  const missingRequired = columns.filter((c) => isRequiredColumn(c) && !mapping[c.name]);
  const samplesByHeader = useMemo(
    () => Object.fromEntries((analysis?.columns ?? []).map((c) => [c.name, c.samples])),
    [analysis],
  );
  const reviewRows = (preview?.rows ?? []).filter((row) => reviewFilter === "all" || row.status === reviewFilter);
  const categoryDef = categoryColumns.find((c) => c.name === activeCategoryColumn) ?? null;
  const categoryLabel = categoryDef ? (categoryDef.displayName ?? categoryDef.name) : "";
  const reviewColumns = categoryDef ? mappedColumns.filter((c) => c.name !== categoryDef.name) : mappedColumns;

  if (schemaStatus !== "ready" && !snapshot) {
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
        <h1 className="import-page__title">Import into {displayName}</h1>
        <p className="import-page__lead">
          Bank and broker exports work as they come: semicolons, decimal commas, German dates and the account lines above the header.
        </p>
      </header>

      {error ? <Alert tone="error" message={error} /> : null}
      {notice && !error && !result ? <Alert tone="success" message={notice} /> : null}

      <section className="import-section" aria-labelledby="import-file-heading">
        <div className="import-section__head">
          <span className="import-section__step">1</span>
          <h2 id="import-file-heading" className="import-section__title">File</h2>
          {profiles.length > 0 ? (
            <div className="import-section__aside">
              <SelectField
                label="Profile"
                compact
                value={profileId === null ? "" : String(profileId)}
                onChange={(event) => chooseProfile(event.target.value)}
                options={[{ label: "No profile", value: "" }, ...profiles.map((p) => ({ label: p.name, value: String(p.id) }))]}
              />
            </div>
          ) : null}
        </div>

        <FileDrop
          file={file}
          rowCount={analysis?.rowCount ?? null}
          inputRef={fileInputRef}
          onChoose={(chosen) => void chooseFile(chosen)}
        />
        {busy === "analyze" ? <Spinner label="Reading the file" /> : null}
      </section>

      {analysis ? (
        <>
          <section className="import-section" aria-labelledby="import-read-heading">
            <div className="import-section__head">
              <span className="import-section__step">2</span>
              <h2 id="import-read-heading" className="import-section__title">How the file is read</h2>
              <span className="import-section__note">Detected; change anything that looks wrong.</span>
            </div>
            <ReadSettings analysis={analysis} settings={settings} onChange={changeSetting} />
          </section>

          <section className="import-section" aria-labelledby="import-map-heading">
            <div className="import-section__head">
              <span className="import-section__step">3</span>
              <h2 id="import-map-heading" className="import-section__title">Columns</h2>
              <span className="import-section__note">
                Rows whose <em>key</em> columns match a row already in {displayName} are skipped, so an overlapping export adds nothing twice.
              </span>
            </div>
            <table className="import-map">
              <thead>
                <tr>
                  <th scope="col">Column</th>
                  <th scope="col">From file column</th>
                  <th scope="col">Example</th>
                  <th scope="col" className="import-map__key-head">Key</th>
                </tr>
              </thead>
              <tbody>
                {columns.map((column) => (
                  <MappingRow
                    key={column.name}
                    column={column}
                    headers={headers}
                    header={mapping[column.name] ?? ""}
                    sample={isFixed(mapping[column.name]) ? fixedValue(mapping[column.name]) : samplesByHeader[mapping[column.name] ?? ""]?.[0]}
                    isKey={keyColumns.includes(column.name)}
                    onHeaderChange={(header) => {
                      invalidate();
                      setMapping((current) => ({ ...current, [column.name]: header }));
                      if (!header) setKeyColumns((keys) => keys.filter((k) => k !== column.name));
                    }}
                    onKeyChange={(checked) => {
                      invalidate();
                      setKeyColumns((keys) => (checked ? [...keys, column.name] : keys.filter((k) => k !== column.name)));
                    }}
                  />
                ))}
              </tbody>
            </table>
            {missingRequired.length > 0 ? (
              <p className="import-section__warning">
                Required, not mapped: {missingRequired.map((c) => c.displayName ?? c.name).join(", ")}. Every row will be rejected.
              </p>
            ) : null}
            <div className="import-actions">
              <Button
                type="button"
                variant="primary"
                onClick={() => void runCheck()}
                disabled={mappedColumns.length === 0 || busy !== null}
              >
                {busy === "preview" ? "Checking…" : preview ? "Check again" : "Check rows"}
              </Button>
              <div className="import-profile">
                <TextField
                  label="Profile name"
                  hideLabel
                  placeholder="Profile name, e.g. DKB Girokonto"
                  value={profileName}
                  onChange={(event) => setProfileName(event.target.value)}
                />
                <Button type="button" variant="ghost" onClick={() => void saveProfile(false)} disabled={busy !== null || mappedColumns.length === 0}>
                  {selectedProfile ? "Update profile" : "Save as profile"}
                </Button>
                {selectedProfile ? (
                  <>
                    <Button type="button" variant="ghost" onClick={() => void saveProfile(true)} disabled={busy !== null}>
                      Save as new
                    </Button>
                    <Button type="button" variant="ghost" onClick={() => void removeProfile()} disabled={busy !== null} aria-label="Delete profile">
                      <Icon name="trash" size={14} />
                    </Button>
                  </>
                ) : null}
              </div>
            </div>
            {profileNotice ? <p className="import-profile__notice" role="status">{profileNotice}</p> : null}
          </section>
        </>
      ) : null}

      {preview ? (
        <section className="import-section" aria-labelledby="import-review-heading">
          <div className="import-section__head">
            <span className="import-section__step">4</span>
            <h2 id="import-review-heading" className="import-section__title">{result ? "Imported" : "Review"}</h2>
          </div>

          {result ? (
            <Alert
              tone="success"
              title={`${countRows(result.inserted)} imported into ${displayName}`}
              message={rulesNotice ? `${skippedSummary(result)} ${rulesNotice}` : skippedSummary(result)}
            />
          ) : categoryColumns.length > 0 ? (
            <CategoriseBar
              columns={categoryColumns}
              column={activeCategoryColumn}
              onColumnChange={setCategoryColumn}
              categorising={categorising}
              rows={categoryRows}
              aiEnabled={aiEnabled}
            />
          ) : null}

          <div className="import-summary" role="tablist" aria-label="Rows by status">
            <SummaryChip label="All" count={preview.total} active={reviewFilter === "all"} onClick={() => setReviewFilter("all")} />
            <SummaryChip label={result ? "Imported" : "New"} count={preview.new} tone="new" active={reviewFilter === "new"} onClick={() => setReviewFilter("new")} />
            <SummaryChip label="Already there" count={preview.duplicates} tone="duplicate" active={reviewFilter === "duplicate"} onClick={() => setReviewFilter("duplicate")} />
            <SummaryChip label="Problems" count={preview.invalid} tone="invalid" active={reviewFilter === "invalid"} onClick={() => setReviewFilter("invalid")} />
          </div>

          <div className="import-review">
            <table className="import-review__table">
              <thead>
                <tr>
                  <th scope="col">Line</th>
                  <th scope="col">Status</th>
                  {reviewColumns.map((column) => (
                    <th key={column.name} scope="col">{column.displayName ?? column.name}</th>
                  ))}
                  {categoryDef ? <th scope="col">{categoryLabel}</th> : null}
                </tr>
              </thead>
              <tbody>
                {reviewRows.slice(0, REVIEW_ROW_LIMIT).map((row) => {
                  const errorFields = new Set(row.errors.map((e) => e.field));
                  return (
                    <tr key={row.line} className={`import-review__row import-review__row--${row.status}`}>
                      <td className="import-review__line">{row.line}</td>
                      <td>
                        <span className={`import-status import-status--${row.status}`}>
                          {row.status === "new" ? (result ? "Imported" : "New") : row.status === "duplicate" ? "Already there" : "Problem"}
                        </span>
                        {row.errors.length > 0 ? (
                          <ul className="import-review__errors">
                            {row.errors.map((e, index) => (
                              <li key={index}>{e.message}</li>
                            ))}
                          </ul>
                        ) : null}
                      </td>
                      {reviewColumns.map((column) => (
                        <td
                          key={column.name}
                          className={errorFields.has(column.name) ? "import-review__cell import-review__cell--error" : "import-review__cell"}
                        >
                          {row.display[column.name] ?? <span className="import-review__empty">—</span>}
                        </td>
                      ))}
                      {categoryDef ? (
                        <td className={errorFields.has(categoryDef.name) ? "import-review__cell import-review__cell--error" : "import-review__cell"}>
                          {!result && row.status === "new" && !row.display[categoryDef.name] ? (
                            <CategoryCell
                              rowKey={String(row.line)}
                              label={categoryLabel}
                              options={categorising.options}
                              choice={categorising.choices[String(row.line)]}
                              categorising={categorising}
                            />
                          ) : (
                            (row.display[categoryDef.name] ?? <span className="import-review__empty">—</span>)
                          )}
                        </td>
                      ) : null}
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {reviewRows.length > REVIEW_ROW_LIMIT || preview.rowsTruncated ? (
              <p className="import-section__note">Showing the first {REVIEW_ROW_LIMIT} rows of this list.</p>
            ) : null}
            {reviewRows.length === 0 ? <p className="import-section__note">No rows in this list.</p> : null}
          </div>

          <div className="import-actions">
            {result ? (
              <>
                <Button type="button" variant="primary" onClick={() => navigate(`/data/${tableId}`)}>
                  Open {displayName}
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  onClick={() => {
                    setFile(null);
                    setContent(null);
                    setAnalysis(null);
                    invalidate();
                    fileInputRef.current?.click();
                  }}
                >
                  Import another file
                </Button>
              </>
            ) : (
              <Button
                type="button"
                variant="primary"
                onClick={() => void runImportNow()}
                disabled={preview.new === 0 || busy !== null}
              >
                {busy === "import"
                  ? "Importing…"
                  : preview.new === 0
                    ? "Nothing new to import"
                    : `Import ${preview.new} ${preview.new === 1 ? "row" : "rows"}`}
              </Button>
            )}
          </div>
        </section>
      ) : null}
    </div>
  );
}

function MappingRow({
  column,
  headers,
  header,
  sample,
  isKey,
  onHeaderChange,
  onKeyChange,
}: {
  column: ModelColumn;
  headers: string[];
  header: string;
  sample: string | undefined;
  isKey: boolean;
  onHeaderChange: (header: string) => void;
  onKeyChange: (checked: boolean) => void;
}) {
  const label = column.displayName ?? column.name;
  const required = isRequiredColumn(column);
  const fixed = isFixed(header);
  return (
    <tr className={header ? "import-map__row import-map__row--mapped" : "import-map__row"}>
      <th scope="row" className="import-map__column">
        <span className="import-map__name">{label}</span>
        <span className="import-map__type">
          {column.type}
          {required ? <span className="import-map__required"> · required</span> : null}
        </span>
      </th>
      <td>
        <select
          className="field__control field__control--compact import-map__select"
          aria-label={`File column for ${label}`}
          value={fixed ? FIXED : header}
          onChange={(event) => onHeaderChange(event.target.value === FIXED ? asFixed(fixedValue(header)) : event.target.value)}
        >
          <option value="">Don't import</option>
          {headers.map((h) => (
            <option key={h} value={h}>
              {h}
            </option>
          ))}
          <option value={FIXED}>Same value for every row…</option>
        </select>
        {fixed ? <FixedValue column={column} label={label} value={fixedValue(header)} onChange={(value) => onHeaderChange(asFixed(value))} /> : null}
      </td>
      <td className="import-map__sample">{header ? sample || <span className="import-review__empty">empty</span> : null}</td>
      <td className="import-map__key">
        <input
          type="checkbox"
          aria-label={`${label} identifies a row`}
          checked={isKey}
          disabled={!header}
          onChange={(event) => onKeyChange(event.target.checked)}
        />
      </td>
    </tr>
  );
}

/**
 * The value every imported row gets — for a link a record by its name (the
 * account a bank export belongs to), otherwise typed in.
 */
function FixedValue({ column, label, value, onChange }: { column: ModelColumn; label: string; value: string; onChange: (value: string) => void }) {
  const snapshot = useSchemaStore((state) => state.snapshot);
  const target = column.type === "reference" ? (snapshot?.tables.find((t) => t.name === column.references) ?? null) : null;
  const [options, setOptions] = useState<string[]>([]);
  useEffect(() => {
    if (!target) return;
    let cancelled = false;
    const labelColumn = getDisplayColumnForTable(target);
    fetchTableRows(target.name, { page: 1, perPage: 200, sort: [], search: "", filters: [] })
      .then((response) => {
        if (cancelled) return;
        const labels = response.data.map((row) => String(row[labelColumn] ?? row.id ?? "")).filter(Boolean);
        setOptions([...new Set(labels)]);
      })
      .catch(() => !cancelled && setOptions([]));
    return () => {
      cancelled = true;
    };
  }, [target]);

  if (target) {
    return (
      <select
        className="field__control field__control--compact import-map__fixed"
        aria-label={`Value of ${label} for every row`}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      >
        <option value="">Choose…</option>
        {options.map((option) => (
          <option key={option} value={option}>
            {option}
          </option>
        ))}
      </select>
    );
  }
  return (
    <input
      className="field__control field__control--compact import-map__fixed"
      aria-label={`Value of ${label} for every row`}
      placeholder="Value for every row"
      value={value}
      onChange={(event) => onChange(event.target.value)}
    />
  );
}

function SummaryChip({
  label,
  count,
  tone,
  active,
  onClick,
}: {
  label: string;
  count: number;
  tone?: ImportRowStatus;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      className={["import-chip", tone ? `import-chip--${tone}` : "", active ? "import-chip--active" : ""].filter(Boolean).join(" ")}
      onClick={onClick}
    >
      <span className="import-chip__count">{count}</span>
      <span className="import-chip__label">{label}</span>
    </button>
  );
}
