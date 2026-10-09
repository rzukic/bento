import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { createForm, deleteForm, getForm, isSection, updateForm, type FieldDefault, type FormDefinition, type FormField, type FormItem } from "../../api/forms";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { useFormsStore } from "../../stores/formsStore";
import { useSchemaStore } from "../../stores/schemaStore";
import type { ModelColumn, ModelTable } from "../../types/api";
import { FormFill } from "./FormFill";
import { columnLabel, defaultForm, fieldKind, isFormWritable, isNumeric, isRequired, missingRequired } from "./formModel";
import "./Forms.css";

type DefaultChoice = "none" | FieldDefault["kind"];

function defaultChoices(column: ModelColumn): { value: DefaultChoice; label: string }[] {
  const kind = fieldKind(column);
  return [
    { value: "none", label: "Empty" },
    ...(kind === "date" || kind === "datetime" ? [{ value: "today" as const, label: "Today" }] : []),
    ...(kind === "date" || kind === "datetime" ? [{ value: "now" as const, label: "Now" }] : []),
    { value: "last", label: "Last used" },
    { value: "value", label: "Fixed value" },
  ];
}

/**
 * The form builder (Step 4.4, Part 10 R10.2, light): fields in order, sections,
 * labels and help, hidden fields with defaults, a +/− switch for amounts, and
 * whether the form is shared and listed under "Quick add". A live preview
 * beside it shows the form as it will be filled.
 */
export function FormBuilder() {
  const { formId } = useParams();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const isNew = formId === undefined;
  const snapshot = useSchemaStore((s) => s.snapshot);
  const loadSchema = useSchemaStore((s) => s.load);
  const reloadForms = useFormsStore((s) => s.load);

  const [name, setName] = useState("");
  const [isShared, setIsShared] = useState(true);
  const [form, setForm] = useState<FormDefinition | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [open, setOpen] = useState<number | null>(null);
  // Dragging an item by its grip (pointer events: mouse, touch and pen alike):
  // which one, and the gap it would land in (0 = before the first item).
  const [dragging, setDragging] = useState<number | null>(null);
  const [dropAt, setDropAt] = useState<number | null>(null);
  const listRef = useRef<HTMLOListElement>(null);

  useEffect(() => {
    void loadSchema();
  }, [loadSchema]);

  useEffect(() => {
    if (isNew) {
      const tableName = searchParams.get("table");
      const table = snapshot?.tables.find((t) => t.name === tableName);
      if (!snapshot) return;
      if (!table) {
        setError(`Choose a table for the form; '${tableName ?? ""}' does not exist.`);
        return;
      }
      setForm((current) => current ?? { ...defaultForm(table), pinned: false });
      setName((current) => current || `Add ${(table.displayName ?? table.name).toLowerCase().replace(/s$/, "")}`);
      return;
    }
    getForm(Number(formId))
      .then((saved) => {
        setForm(saved.form);
        setName(saved.name);
        setIsShared(saved.isShared);
      })
      .catch(() => setError("The form could not be loaded."));
  }, [formId, isNew, searchParams, snapshot]);

  const table: ModelTable | undefined = form ? snapshot?.tables.find((t) => t.name === form.table) : undefined;
  const used = useMemo(() => new Set(form?.fields.flatMap((f) => (isSection(f) ? [] : [f.column])) ?? []), [form]);
  const unused = table ? table.columns.filter((c) => isFormWritable(c) && !used.has(c.name)) : [];
  const missing = form && table ? missingRequired(form, table) : [];

  if (error && !form) return <div className="forms-page"><Alert tone="error" message={error} /></div>;
  if (!form || !table) return <div className="forms-page"><Spinner label="Loading the form" /></div>;

  const setItems = (fields: FormItem[]) => setForm({ ...form, fields });
  const setItem = (index: number, item: FormItem) => setItems(form.fields.map((f, i) => (i === index ? item : f)));
  const move = (index: number, delta: number) => {
    const target = index + delta;
    if (target < 0 || target >= form.fields.length) return;
    const fields = [...form.fields];
    [fields[index], fields[target]] = [fields[target]!, fields[index]!];
    setItems(fields);
    setOpen((o) => (o === index ? target : o === target ? index : o));
  };
  /** Moves an item to a gap between items (0 = before the first); the open one stays open. */
  const moveTo = (from: number, gap: number) => {
    const target = gap > from ? gap - 1 : gap;
    if (target === from) return;
    const openItem = open === null ? null : form.fields[open];
    const fields = [...form.fields];
    const [item] = fields.splice(from, 1);
    fields.splice(target, 0, item!);
    setItems(fields);
    setOpen(openItem ? fields.indexOf(openItem) : null);
  };
  const endDrag = () => {
    setDragging(null);
    setDropAt(null);
  };
  /** The gap under the pointer: before the first item whose middle lies below it. */
  const gapAt = (clientY: number) => {
    const rows = [...(listRef.current?.children ?? [])];
    const index = rows.findIndex((row) => {
      const box = row.getBoundingClientRect();
      return clientY < box.top + box.height / 2;
    });
    return index < 0 ? rows.length : index;
  };
  const remove = (index: number) => {
    setItems(form.fields.filter((_, i) => i !== index));
    setOpen(null);
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const saved = isNew ? await createForm(name.trim(), form, isShared) : await updateForm(Number(formId), { name: name.trim(), form, isShared });
      await reloadForms({ force: true });
      navigate(`/forms/${saved.id}`);
    } catch (cause) {
      setError(cause instanceof ApiClientError ? (cause.status === 403 ? "Only the form's owner can change it." : cause.message) : "The form could not be saved.");
    } finally {
      setSaving(false);
    }
  };

  const removeForm = async () => {
    setConfirmDelete(false);
    try {
      await deleteForm(Number(formId));
      await reloadForms({ force: true });
      navigate("/forms");
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The form could not be deleted.");
    }
  };

  return (
    <div className="forms-page forms-page--builder">
      <header>
        <h1 className="forms-page__title">{isNew ? "New form" : "Edit form"}</h1>
        <p className="forms-page__lead">Adds rows to {table.displayName ?? table.name}.</p>
      </header>

      <div className="form-builder">
        <section className="form-builder__editor" aria-label="Form settings">
          <label className="form-field">
            <span className="form-field__label">Name</span>
            <input className="field__control" value={name} aria-label="Form name" onChange={(event) => setName(event.target.value)} />
          </label>
          <div className="form-builder__checks">
            <label className="form-builder__check">
              <input type="checkbox" checked={Boolean(form.pinned)} onChange={(event) => setForm({ ...form, pinned: event.target.checked })} />
              Quick add — in the sidebar and on dashboards showing {table.displayName ?? table.name}
            </label>
            <label className="form-builder__check">
              <input type="checkbox" checked={isShared} onChange={(event) => setIsShared(event.target.checked)} />
              Shared with everyone who signs in
            </label>
          </div>

          <ol ref={listRef} className={dragging === null ? "form-builder__items" : "form-builder__items form-builder__items--dragging"} aria-label="Fields">
            {form.fields.map((item, index) => {
              const column = isSection(item) ? null : table.columns.find((c) => c.name === item.column) ?? null;
              const title = isSection(item) ? item.section || "Section" : column ? columnLabel(column, item) : `${item.column} (gone)`;
              return (
                <li
                  key={isSection(item) ? `s${index}` : item.column}
                  className={[
                    "form-builder__item",
                    isSection(item) ? "form-builder__item--section" : "",
                    dragging === index ? "form-builder__item--dragging" : "",
                    dragging !== null && dropAt === index ? "form-builder__item--drop-before" : "",
                    dragging !== null && dropAt === index + 1 && index === form.fields.length - 1 ? "form-builder__item--drop-after" : "",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                >
                  <div className="form-builder__item-head">
                    <span
                      className="form-builder__grip"
                      title="Drag to move"
                      aria-hidden="true"
                      onPointerDown={(event) => {
                        if (event.button > 0) return;
                        event.preventDefault();
                        event.currentTarget.setPointerCapture?.(event.pointerId);
                        setDragging(index);
                        setDropAt(index);
                      }}
                      onPointerMove={(event) => {
                        if (dragging === index) setDropAt(gapAt(event.clientY));
                      }}
                      onPointerUp={(event) => {
                        if (dragging !== index) return;
                        moveTo(index, gapAt(event.clientY));
                        endDrag();
                      }}
                      onPointerCancel={endDrag}
                    >
                      <Icon name="grip" size={13} />
                    </span>
                    <button
                      type="button"
                      className="form-builder__item-title"
                      aria-expanded={open === index}
                      onClick={() => setOpen(open === index ? null : index)}
                    >
                      <Icon name={isSection(item) ? "type" : open === index ? "chevron-down" : "chevron-right"} size={13} />
                      <span>{title}</span>
                      {!isSection(item) && item.hidden ? <span className="badge">hidden</span> : null}
                      {!isSection(item) && column && isRequired(column) ? <span className="form-builder__required">required</span> : null}
                    </button>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Move ${title} up`} disabled={index === 0} onClick={() => move(index, -1)}>
                      <Icon name="chevron-up" size={13} />
                    </Button>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Move ${title} down`} disabled={index === form.fields.length - 1} onClick={() => move(index, 1)}>
                      <Icon name="chevron-down" size={13} />
                    </Button>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Remove ${title}`} onClick={() => remove(index)}>
                      <Icon name="x" size={13} />
                    </Button>
                  </div>
                  {open === index ? (
                    isSection(item) ? (
                      <div className="form-builder__settings">
                        <label className="form-field">
                          <span className="form-field__label">Section title</span>
                          <input className="field__control" value={item.section} aria-label="Section title" onChange={(event) => setItem(index, { section: event.target.value })} />
                        </label>
                      </div>
                    ) : column ? (
                      <FieldSettings field={item} column={column} onChange={(next) => setItem(index, next)} />
                    ) : null
                  ) : null}
                </li>
              );
            })}
          </ol>

          <div className="form-builder__add">
            {unused.length > 0 ? (
              <select
                className="field__control field__control--compact"
                aria-label="Add a field"
                value=""
                onChange={(event) => {
                  if (!event.target.value) return;
                  setItems([...form.fields, { column: event.target.value }]);
                  setOpen(form.fields.length);
                }}
              >
                <option value="">Add a field…</option>
                {unused.map((c) => (
                  <option key={c.name} value={c.name}>
                    {c.displayName ?? c.name}
                  </option>
                ))}
              </select>
            ) : null}
            <Button
              type="button"
              size="sm"
              variant="ghost"
              leadingIcon={<Icon name="plus" size={12} />}
              onClick={() => {
                setItems([...form.fields, { section: "Details" }]);
                setOpen(form.fields.length);
              }}
            >
              Section
            </Button>
          </div>

          {missing.length > 0 ? (
            <Alert tone="warning" message={`${table.displayName ?? table.name} needs ${missing.map((c) => columnLabel(c)).join(", ")}; add ${missing.length === 1 ? "it" : "them"} to the form.`} />
          ) : null}
          {error ? <Alert tone="error" message={error} /> : null}

          <div className="form-builder__actions">
            {!isNew ? (
              <Button type="button" variant="ghost" aria-label="Delete form" onClick={() => setConfirmDelete(true)}>
                <Icon name="trash" size={14} />
              </Button>
            ) : null}
            <Button type="button" variant="ghost" onClick={() => navigate(isNew ? "/forms" : `/forms/${formId}`)}>
              Cancel
            </Button>
            <Button type="button" variant="primary" disabled={saving || !name.trim() || missing.length > 0} onClick={() => void save()}>
              {saving ? "Saving…" : isNew ? "Create form" : "Save"}
            </Button>
          </div>
        </section>

        <section className="form-builder__preview" aria-label="Preview">
          <span className="section-label">Preview</span>
          <FormFill key={JSON.stringify(form)} formKey={`preview-${table.name}`} form={form} table={table} preview />
        </section>
      </div>

      <ConfirmDialog
        open={confirmDelete}
        title="Delete this form?"
        description="Rows added with it stay. Quick add and links to it stop working."
        confirmLabel="Delete"
        confirmVariant="danger"
        onConfirm={() => void removeForm()}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}

/** Label, help, default, sign and hidden for one field. */
function FieldSettings({ field, column, onChange }: { field: FormField; column: ModelColumn; onChange: (field: FormField) => void }) {
  const choice: DefaultChoice = field.default?.kind ?? "none";
  const fixed = field.default?.kind === "value" ? field.default.value : "";
  const set = (changes: Partial<FormField>) => {
    const next: FormField = { ...field, ...changes };
    for (const key of Object.keys(next) as (keyof FormField)[]) if (next[key] === undefined || next[key] === "") delete next[key];
    onChange(next);
  };
  const hiddenAllowed = choice === "today" || choice === "now" || choice === "value";
  return (
    <div className="form-builder__settings">
      <label className="form-field">
        <span className="form-field__label">Label</span>
        <input className="field__control" value={field.label ?? ""} placeholder={column.displayName ?? column.name} aria-label="Label" onChange={(event) => set({ label: event.target.value })} />
      </label>
      <label className="form-field">
        <span className="form-field__label">Help</span>
        <input className="field__control" value={field.help ?? ""} placeholder="A line under the field" aria-label="Help" onChange={(event) => set({ help: event.target.value })} />
      </label>
      <div className="form-builder__row">
        <label className="form-field">
          <span className="form-field__label">Starts with</span>
          <select
            className="field__control"
            aria-label="Default"
            value={choice}
            onChange={(event) => {
              const value = event.target.value as DefaultChoice;
              set({
                default: value === "none" ? undefined : value === "value" ? { kind: "value", value: "" } : { kind: value },
                hidden: value === "none" || value === "last" ? undefined : field.hidden,
              });
            }}
          >
            {defaultChoices(column).map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
        {choice === "value" ? (
          <label className="form-field">
            <span className="form-field__label">Value</span>
            <input
              className="field__control"
              aria-label="Fixed value"
              value={fixed === null || fixed === undefined ? "" : String(fixed)}
              onChange={(event) => {
                const raw = event.target.value;
                const numeric = isNumeric(column) && raw.trim() !== "" && Number.isFinite(Number(raw.replace(",", ".")));
                set({ default: { kind: "value", value: numeric ? Number(raw.replace(",", ".")) : fieldKind(column) === "boolean" ? raw === "true" : raw } });
              }}
            />
          </label>
        ) : null}
        {isNumeric(column) ? (
          <label className="form-field">
            <span className="form-field__label">+/− switch</span>
            <select className="field__control" aria-label="Sign switch" value={field.sign ?? ""} onChange={(event) => set({ sign: (event.target.value || undefined) as FormField["sign"] })}>
              <option value="">None</option>
              <option value="negative">Starts at − (spending)</option>
              <option value="positive">Starts at +</option>
            </select>
          </label>
        ) : null}
      </div>
      <label className="form-builder__check">
        <input type="checkbox" checked={Boolean(field.hidden)} disabled={!hiddenAllowed} onChange={(event) => set({ hidden: event.target.checked || undefined })} />
        Hidden — filled by its default{hiddenAllowed ? "" : " (choose Today, Now or a fixed value first)"}
      </label>
    </div>
  );
}
