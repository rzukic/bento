import { useEffect, useMemo, useState } from "react";
import { fetchTableRows } from "../../api/data";
import type { FormField } from "../../api/forms";
import { useSchemaStore } from "../../stores/schemaStore";
import type { ModelColumn } from "../../types/api";
import { DEFAULT_QUERY } from "../../types/explorer";
import { findTable, getDisplayColumnForTable, getReferencePrimaryKey } from "../data-explorer/components/editors/referenceMetadata";
import { columnLabel, fieldKind, isRequired, referenceTable, shiftDays, todayIso } from "./formModel";

/** At most this many choices show as buttons; more get a search. */
const CHIPS_MAX = 12;

type FieldProps = {
  id: string;
  field: FormField;
  column: ModelColumn;
  value: unknown;
  sign?: "negative" | "positive";
  error?: string | null;
  /** Values typed before in this text field, newest first. */
  recent?: string[];
  autoFocus?: boolean;
  onChange: (value: unknown) => void;
  onSign: (sign: "negative" | "positive") => void;
};

/** One field of a form, sized for a thumb: big targets, the phone's own keyboards and pickers. */
export function FormFieldInput({ id, field, column, value, sign, error, recent, autoFocus, onChange, onSign }: FieldProps) {
  const label = columnLabel(column, field);
  const kind = fieldKind(column);
  const required = isRequired(column);
  const describedBy = [field.help ? `${id}-help` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;
  const text = typeof value === "string" ? value : value === null || value === undefined ? "" : String(value);

  let control: React.ReactNode;
  switch (kind) {
    case "reference":
      control = <ReferenceChoice id={id} column={column} label={label} value={value} required={required} onChange={onChange} />;
      break;
    case "enum":
      control = (
        <Choices
          id={id}
          label={label}
          options={(column.values ?? []).map((v) => ({ value: v, label: v }))}
          value={value}
          clearable={!required}
          onChange={onChange}
        />
      );
      break;
    case "boolean":
      control = (
        <Choices
          id={id}
          label={label}
          options={[
            { value: true, label: "Yes" },
            { value: false, label: "No" },
          ]}
          value={value}
          clearable={!required}
          onChange={onChange}
        />
      );
      break;
    case "date":
      control = (
        <div className="form-field__row">
          <input
            id={id}
            className="field__control form-field__input"
            type="date"
            value={text}
            required={required}
            aria-describedby={describedBy}
            aria-invalid={error ? true : undefined}
            autoFocus={autoFocus}
            onChange={(event) => onChange(event.target.value)}
          />
          <div className="form-chips" role="group" aria-label={`${label}: quick choices`}>
            {[
              { label: "Today", value: todayIso() },
              { label: "Yesterday", value: shiftDays(todayIso(), -1) },
            ].map((choice) => (
              <button
                key={choice.label}
                type="button"
                className="form-chip"
                aria-pressed={text === choice.value}
                onClick={() => onChange(choice.value)}
              >
                {choice.label}
              </button>
            ))}
          </div>
        </div>
      );
      break;
    case "datetime":
      control = (
        <input
          id={id}
          className="field__control form-field__input"
          type="datetime-local"
          value={text.slice(0, 16)}
          required={required}
          aria-describedby={describedBy}
          aria-invalid={error ? true : undefined}
          autoFocus={autoFocus}
          onChange={(event) => onChange(event.target.value)}
        />
      );
      break;
    case "integer":
    case "number":
      control = (
        <div className="form-field__row">
          {field.sign ? (
            <div className="form-sign" role="group" aria-label={`${label}: sign`}>
              <button type="button" className="form-sign__option" aria-pressed={sign === "negative"} aria-label="Negative" onClick={() => onSign("negative")}>
                −
              </button>
              <button type="button" className="form-sign__option" aria-pressed={sign === "positive"} aria-label="Positive" onClick={() => onSign("positive")}>
                +
              </button>
            </div>
          ) : null}
          <input
            id={id}
            className="field__control form-field__input form-field__input--number"
            type="text"
            inputMode={kind === "integer" && !field.sign ? "numeric" : "decimal"}
            autoComplete="off"
            value={text}
            placeholder={column.type === "currency" ? "0,00" : "0"}
            required={required}
            aria-describedby={describedBy}
            aria-invalid={error ? true : undefined}
            autoFocus={autoFocus}
            onChange={(event) => onChange(event.target.value)}
          />
        </div>
      );
      break;
    case "json":
      control = (
        <textarea
          id={id}
          className="field__control form-field__input mono"
          rows={3}
          value={text}
          aria-describedby={describedBy}
          aria-invalid={error ? true : undefined}
          onChange={(event) => onChange(event.target.value)}
        />
      );
      break;
    default:
      control = (
        <>
          <input
            id={id}
            className="field__control form-field__input"
            type="text"
            value={text}
            required={required}
            list={recent && recent.length > 0 ? `${id}-recent` : undefined}
            autoComplete="off"
            aria-describedby={describedBy}
            aria-invalid={error ? true : undefined}
            autoFocus={autoFocus}
            onChange={(event) => onChange(event.target.value)}
          />
          {recent && recent.length > 0 ? (
            <datalist id={`${id}-recent`}>
              {recent.map((option) => (
                <option key={option} value={option} />
              ))}
            </datalist>
          ) : null}
        </>
      );
  }

  // Choices label themselves through their group; inputs get a <label>.
  const grouped = kind === "reference" || kind === "enum" || kind === "boolean";
  return (
    <div className={error ? "form-field form-field--error" : "form-field"}>
      {grouped ? (
        <span className="form-field__label" id={`${id}-label`}>
          {label}
          {required ? <span className="form-field__required" aria-hidden="true"> *</span> : null}
        </span>
      ) : (
        <label className="form-field__label" htmlFor={id}>
          {label}
          {required ? <span className="form-field__required" aria-hidden="true"> *</span> : null}
        </label>
      )}
      {control}
      {field.help ? (
        <span className="form-field__help" id={`${id}-help`}>
          {field.help}
        </span>
      ) : null}
      {error ? (
        <span className="form-field__error" id={`${id}-error`} role="alert">
          {error}
        </span>
      ) : null}
    </div>
  );
}

type Option = { value: unknown; label: string };

/** A choice of a few options as buttons; tapping the chosen one again clears it when the field may be empty. */
function Choices({ id, label, options, value, clearable, onChange }: { id: string; label: string; options: Option[]; value: unknown; clearable: boolean; onChange: (value: unknown) => void }) {
  if (options.length > CHIPS_MAX) {
    const index = options.findIndex((o) => o.value === value);
    return (
      <select id={id} className="field__control form-field__input" aria-label={label} value={index < 0 ? "" : String(index)} onChange={(event) => onChange(event.target.value === "" ? null : options[Number(event.target.value)]!.value)}>
        <option value="">—</option>
        {options.map((option, i) => (
          <option key={i} value={String(i)}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }
  return (
    <div className="form-chips" role="group" aria-labelledby={`${id}-label`} id={id}>
      {options.map((option, i) => {
        const chosen = option.value === value;
        return (
          <button
            key={i}
            type="button"
            className="form-chip"
            aria-pressed={chosen}
            onClick={() => onChange(chosen && clearable ? null : option.value)}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

/**
 * A link to another table's row: its records as buttons when there are few
 * (accounts, categories), otherwise a search with the closest matches.
 */
function ReferenceChoice({ id, column, label, value, required, onChange }: { id: string; column: ModelColumn; label: string; value: unknown; required: boolean; onChange: (value: unknown) => void }) {
  const snapshot = useSchemaStore((s) => s.snapshot);
  const tableName = referenceTable(column);
  const table = findTable(snapshot, tableName);
  const display = getDisplayColumnForTable(table);
  const key = getReferencePrimaryKey(column);
  const [options, setOptions] = useState<Option[] | null>(null);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState("");
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!tableName) return;
    let cancelled = false;
    const timer = window.setTimeout(
      () => {
        fetchTableRows(tableName, { ...DEFAULT_QUERY, perPage: search ? 8 : 50, search: search.trim(), sort: [{ column: display, dir: "asc" }] })
          .then((result) => {
            if (cancelled) return;
            setOptions(result.data.map((row) => ({ value: row[key], label: row[display] === null || row[display] === undefined ? String(row[key]) : String(row[display]) })));
            if (!search) setTotal(result.meta?.total ?? result.data.length);
          })
          .catch(() => !cancelled && setFailed(true));
      },
      search ? 200 : 0,
    );
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [tableName, search, display, key]);

  const chosenLabel = useMemo(() => options?.find((o) => o.value === value)?.label ?? null, [options, value]);
  const [knownLabel, setKnownLabel] = useState<string | null>(null);
  useEffect(() => {
    if (chosenLabel) setKnownLabel(chosenLabel);
  }, [chosenLabel]);

  if (failed) return <span className="form-field__help">The choices could not be loaded.</span>;
  if (options === null) return <span className="form-field__help">Loading…</span>;
  if (total <= CHIPS_MAX && !search) return <Choices id={id} label={label} options={options} value={value} clearable={!required} onChange={onChange} />;

  return (
    <div className="form-reference">
      {value !== null && value !== undefined && value !== "" ? (
        <div className="form-chips">
          <button type="button" className="form-chip" aria-pressed="true" aria-label={`${label}: ${chosenLabel ?? knownLabel ?? String(value)}, tap to clear`} onClick={() => onChange(null)}>
            {chosenLabel ?? knownLabel ?? String(value)} ×
          </button>
        </div>
      ) : null}
      <input
        id={id}
        className="field__control form-field__input"
        type="search"
        value={search}
        placeholder={`Search ${table?.displayName ?? tableName ?? ""}`}
        aria-label={`Search ${label}`}
        onChange={(event) => setSearch(event.target.value)}
      />
      <div className="form-chips" role="group" aria-labelledby={`${id}-label`}>
        {options.slice(0, 8).map((option, i) => (
          <button
            key={i}
            type="button"
            className="form-chip"
            aria-pressed={option.value === value}
            onClick={() => {
              setKnownLabel(option.label);
              onChange(option.value);
              setSearch("");
            }}
          >
            {option.label}
          </button>
        ))}
      </div>
    </div>
  );
}
