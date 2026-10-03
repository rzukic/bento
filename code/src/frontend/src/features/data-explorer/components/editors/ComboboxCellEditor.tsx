import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from "react";
import { Icon } from "../../../../components/common/Icon";
import { EditorShell } from "./editorShell";
import { FloatingPanel } from "./FloatingPanel";

export type ComboboxOption = {
  /** Stable identity used by the consumer when committing. */
  value: string;
  /** Human label shown in the listbox and trigger. */
  label: string;
};

type Props = {
  /** Currently selected option value (the consumer's serialized form). */
  value: string | null;
  /**
   * Display label for the current value. Used for the closed trigger when no
   * search query is active. If null/undefined, falls back to `value`.
   */
  displayLabel?: string | null;
  /** Whether the field is allowed to be cleared to null. */
  nullable: boolean;
  /** Accessible label for the trigger + listbox. */
  ariaLabel: string;
  /** Optional placeholder shown on the trigger when no value is set. */
  triggerPlaceholder?: string;
  /** Provide options for a given query string. May return a Promise. */
  loadOptions: (query: string) => ComboboxOption[] | Promise<ComboboxOption[]>;
  /**
   * If true, the combobox debounces and refetches options as the user types.
   * If false (sync), `loadOptions` is called immediately on every change.
   */
  asyncSearch?: boolean;
  /** Editor shared error string (red underline + alert). */
  error?: string | null;
  /** Auto-open + focus on mount (used by inline grid editing). */
  autoFocus?: boolean;
  onCommit: (next: string | null) => void;
  onCancel: () => void;
  /**
   * How the static cell shows the current value (a chip). Rendered over the
   * empty search field at the static value's position, so the value stays
   * visible until the user types.
   */
  renderValue?: (value: string, label: string) => ReactNode;
  /** How an option looks in the list (the same chip as in the cell). */
  renderOption?: (option: ComboboxOption) => ReactNode;
};

const DEBOUNCE_MS = 200;

export function ComboboxCellEditor({
  value,
  displayLabel,
  nullable,
  ariaLabel,
  triggerPlaceholder,
  loadOptions,
  asyncSearch = false,
  error,
  autoFocus,
  onCommit,
  onCancel,
  renderValue,
  renderOption,
}: Props) {
  const [open, setOpen] = useState(Boolean(autoFocus));
  const [query, setQuery] = useState("");
  const [options, setOptions] = useState<ComboboxOption[]>([]);
  const [highlight, setHighlight] = useState(0);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const listRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (autoFocus) inputRef.current?.focus();
  }, [autoFocus]);

  // Close on outside pointerdown. Using pointerdown (not click) so the
  // dropdown collapses before a subsequent focus/click reopens it; we also
  // call onCancel to exit edit mode cleanly in the grid and record editor.
  useEffect(() => {
    if (!open) return;
    function handlePointerDown(event: PointerEvent) {
      const root = rootRef.current;
      if (!root) return;
      if (event.target instanceof Node && root.contains(event.target)) return;
      // The listbox lives in a portal on document.body.
      if (event.target instanceof Node && listRef.current?.contains(event.target)) return;
      setOpen(false);
      onCancel();
    }
    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [open, onCancel]);

  // Resolve options. Sync providers are called immediately; async ones are
  // debounced. We always call once on open so the user sees a starter list.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const run = () => {
      const result = loadOptions(query);
      if (result instanceof Promise) {
        void result.then((opts) => {
          if (!cancelled) {
            setOptions(opts);
            setHighlight(0);
          }
        });
      } else {
        setOptions(result);
        setHighlight(0);
      }
    };
    if (asyncSearch) {
      const handle = setTimeout(run, DEBOUNCE_MS);
      return () => {
        cancelled = true;
        clearTimeout(handle);
      };
    }
    run();
    return () => {
      cancelled = true;
    };
    // loadOptions is intentionally excluded — consumers typically pass a new
    // identity per render. We re-fetch only when the query or open state
    // change, which matches the enum-filter behaviour.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, query, asyncSearch]);

  function pick(option: ComboboxOption) {
    onCommit(option.value);
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      onCancel();
      return;
    }
    if (event.key === "ArrowDown") {
      event.preventDefault();
      setOpen(true);
      setHighlight((h) =>
        options.length === 0 ? 0 : Math.min(h + 1, options.length - 1),
      );
      return;
    }
    if (event.key === "ArrowUp") {
      event.preventDefault();
      setHighlight((h) => Math.max(h - 1, 0));
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      const picked = options[highlight];
      if (picked) pick(picked);
      return;
    }
    if (event.key === "Backspace" && query === "" && nullable && value !== null) {
      event.preventDefault();
      onCommit(null);
    }
  }

  const triggerLabel = useMemo(() => {
    if (value === null || value === undefined || value === "") {
      return triggerPlaceholder ?? "—";
    }
    return displayLabel ?? value;
  }, [value, displayLabel, triggerPlaceholder]);

  const inputValue = open ? query : "";
  const hasValue = value !== null && value !== undefined && value !== "";
  const showValue = Boolean(renderValue) && hasValue && query === "";

  return (
    <EditorShell kind="choice" error={error}>
      <div
        ref={rootRef}
        className="cell-combobox"
        data-open={open || undefined}
      >
        <div
          className={`cell-combobox__trigger${open ? " cell-combobox__trigger--open" : ""}`}
        >
          <input
            ref={inputRef}
            type="text"
            size={1}
            role="combobox"
            aria-expanded={open}
            aria-autocomplete="list"
            aria-label={ariaLabel}
            aria-invalid={Boolean(error) || undefined}
            className="cell-combobox__input"
            placeholder={
              // An empty value reads "—" as in the static cell; typing
              // filters the list below.
              showValue ? "" : open ? (hasValue ? "" : "—") : triggerLabel
            }
            value={inputValue}
            onFocus={() => setOpen(true)}
            onChange={(event) => {
              setQuery(event.target.value);
              setOpen(true);
            }}
            onKeyDown={onKeyDown}
          />
          {showValue && renderValue && value !== null ? (
            <span className="cell-combobox__value" aria-hidden="true">
              <span className="cell-combobox__value-line">{renderValue(value, triggerLabel)}</span>
            </span>
          ) : null}
          {!open ? (
            <span className="cell-combobox__chevron" aria-hidden="true">
              <Icon name="chevron-down" size={14} />
            </span>
          ) : null}
        </div>
        {open ? (
          <FloatingPanel anchorRef={rootRef} panelRef={listRef} matchAnchorWidth>
          <ul
            role="listbox"
            aria-label={ariaLabel}
            className="cell-combobox__listbox"
          >
            {options.length === 0 ? (
              <li
                className="cell-combobox__empty"
                role="option"
                aria-selected={false}
              >
                No matches
              </li>
            ) : (
              options.map((option, index) => {
                const selected = option.value === value;
                const active = index === highlight;
                return (
                  <li
                    key={option.value}
                    role="option"
                    aria-selected={selected}
                    className={[
                      "cell-combobox__option",
                      active ? "cell-combobox__option--active" : "",
                      selected ? "cell-combobox__option--selected" : "",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onMouseEnter={() => setHighlight(index)}
                    onMouseDown={(event) => {
                      // mousedown so the input's blur doesn't cancel before commit
                      event.preventDefault();
                      pick(option);
                    }}
                  >
                    <span className="cell-combobox__check" aria-hidden="true">
                      {selected ? <Icon name="check" size={12} /> : null}
                    </span>
                    <span className="cell-combobox__label">
                      {renderOption ? renderOption(option) : option.label}
                    </span>
                  </li>
                );
              })
            )}
          </ul>
          </FloatingPanel>
        ) : null}
      </div>
    </EditorShell>
  );
}
