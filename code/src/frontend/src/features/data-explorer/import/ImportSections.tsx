import { useState, type RefObject } from "react";
import type { ImportAnalysis, ImportFileSettings } from "../../../api/import";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { SelectField } from "../../../components/ui/SelectField";

const ENCODING_LABELS: Record<string, string> = {
  "utf-8": "UTF-8",
  "windows-1252": "Windows-1252",
  "iso-8859-1": "ISO-8859-1",
  "utf-16": "UTF-16",
};

const DELIMITER_LABELS: Record<string, string> = {
  ";": "Semicolon  ;",
  ",": "Comma  ,",
  "\\t": "Tab",
  "|": "Pipe  |",
};

const DATE_EXAMPLES: Record<string, string> = {
  "dd.MM.yyyy": "31.12.2026",
  "dd.MM.yy": "31.12.26",
  "yyyy-MM-dd": "2026-12-31",
  "dd/MM/yyyy": "31/12/2026",
  "MM/dd/yyyy": "12/31/2026",
  "dd-MM-yyyy": "31-12-2026",
};

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** Drop zone and file input; shows the chosen file once there is one. */
export function FileDrop({
  file,
  rowCount,
  inputRef,
  onChoose,
}: {
  file: File | null;
  rowCount: number | null;
  inputRef: RefObject<HTMLInputElement | null>;
  onChoose: (file: File) => void;
}) {
  const [dragging, setDragging] = useState(false);
  return (
    <div
      className={["import-drop", dragging ? "import-drop--active" : "", file ? "import-drop--chosen" : ""].filter(Boolean).join(" ")}
      onDragOver={(event) => {
        event.preventDefault();
        setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={(event) => {
        event.preventDefault();
        setDragging(false);
        const dropped = event.dataTransfer.files?.[0];
        if (dropped) onChoose(dropped);
      }}
    >
      <input
        ref={inputRef}
        className="import-drop__input"
        type="file"
        accept=".csv,.txt,text/csv,text/plain"
        aria-label="CSV file"
        onChange={(event) => {
          const chosen = event.target.files?.[0];
          if (chosen) onChoose(chosen);
          event.target.value = "";
        }}
      />
      {file ? (
        <div className="import-drop__file">
          <Icon name="table" size={16} />
          <span className="import-drop__name">{file.name}</span>
          <span className="import-drop__meta">
            {formatBytes(file.size)}
            {rowCount !== null ? ` · ${rowCount} rows` : ""}
          </span>
          <Button type="button" variant="ghost" size="sm" onClick={() => inputRef.current?.click()}>
            Choose another
          </Button>
        </div>
      ) : (
        <button type="button" className="import-drop__prompt" onClick={() => inputRef.current?.click()}>
          <Icon name="upload" size={18} className="import-drop__icon" />
          <span>
            <strong>Choose a CSV file</strong> or drop it here
          </span>
        </button>
      )}
    </div>
  );
}

/** Detected encoding, separator, header row and number/date format, editable; plus the skipped preamble lines. */
export function ReadSettings({
  analysis,
  settings,
  onChange,
}: {
  analysis: ImportAnalysis;
  settings: ImportFileSettings;
  onChange: (patch: ImportFileSettings) => void;
}) {
  return (
    <>
      <div className="import-settings">
        <SelectField
          label="Encoding"
          compact
          value={settings.encoding}
          onChange={(event) => onChange({ encoding: event.target.value })}
          options={analysis.options.encodings.map((value) => ({ value, label: ENCODING_LABELS[value] ?? value }))}
        />
        <SelectField
          label="Separator"
          compact
          value={settings.delimiter}
          onChange={(event) => onChange({ delimiter: event.target.value })}
          options={analysis.options.delimiters.map((value) => ({ value, label: DELIMITER_LABELS[value] ?? value }))}
        />
        <SelectField
          label="Header row"
          compact
          value={String(settings.headerLine)}
          onChange={(event) => onChange({ headerLine: Number(event.target.value) })}
          options={Array.from({ length: analysis.preamble.length + 6 }, (_, index) => ({
            value: String(index),
            label: `Row ${index + 1}`,
          }))}
        />
        <SelectField
          label="Decimal separator"
          compact
          value={settings.decimalSeparator}
          onChange={(event) => onChange({ decimalSeparator: event.target.value })}
          options={[
            { value: ",", label: "Comma  1.234,56" },
            { value: ".", label: "Point  1,234.56" },
          ]}
        />
        <SelectField
          label="Dates"
          compact
          value={settings.dateFormat}
          onChange={(event) => onChange({ dateFormat: event.target.value })}
          options={analysis.options.dateFormats.map((value) => ({
            value,
            label: DATE_EXAMPLES[value] ? `${DATE_EXAMPLES[value]}  (${value})` : value,
          }))}
        />
      </div>
      {analysis.preamble.length > 0 ? (
        <details className="import-preamble">
          <summary>
            {analysis.preamble.length} {analysis.preamble.length === 1 ? "line" : "lines"} above the header are skipped
          </summary>
          <ul>
            {analysis.preamble.map((cells, index) => (
              <li key={index}>{cells.filter(Boolean).join("  ·  ")}</li>
            ))}
          </ul>
        </details>
      ) : null}
    </>
  );
}
