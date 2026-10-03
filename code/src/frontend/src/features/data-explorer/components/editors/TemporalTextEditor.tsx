import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type FocusEvent,
  type KeyboardEvent,
  type MouseEvent,
} from "react";
import { Icon } from "../../../../components/common/Icon";
import { EditorShell } from "./editorShell";
import {
  buildMonthKey,
  buildYearOptions,
  buildCalendarDays,
  buildTimeOptions,
  combineDateTime,
  formatDateDraft,
  formatTimeDraft,
  getCurrentLocale,
  getMonthButtonLabel,
  getMonthKey,
  getMonthName,
  getMonthParts,
  getWeekdayLabels,
  getYearRangeLabel,
  parseDateDraft,
  parseTimeDraft,
  shiftMonth,
} from "./temporalEditing";
import type { CellEditorProps } from "./types";

type TemporalTextEditorProps = CellEditorProps<string> & {
  mode: "date" | "datetime";
};

export function TemporalTextEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
  mode,
}: TemporalTextEditorProps) {
  const locale = useMemo(() => getCurrentLocale(), []);
  const isDateTime = mode === "datetime";
  const rootRef = useRef<HTMLDivElement | null>(null);
  const dateInputRef = useRef<HTMLInputElement | null>(null);
  const timeInputRef = useRef<HTMLInputElement | null>(null);

  const [dateDraft, setDateDraft] = useState(formatDateDraft(value, locale));
  const [timeDraft, setTimeDraft] = useState(formatTimeDraft(value));
  const [openPanel, setOpenPanel] = useState<"date" | "time" | null>(null);
  const [datePanelMode, setDatePanelMode] = useState<"days" | "months" | "years">("days");
  const [visibleMonth, setVisibleMonth] = useState(
    getMonthKey(parseDateDraft(formatDateDraft(value, locale), locale) ?? value),
  );
  const [placement, setPlacement] = useState<"down" | "up">("down");

  const selectedDate = parseDateDraft(dateDraft, locale);
  const selectedTime = parseTimeDraft(timeDraft);
  const { year: visibleYear, monthIndex: visibleMonthIndex } = useMemo(
    () => getMonthParts(visibleMonth),
    [visibleMonth],
  );
  const calendarDays = useMemo(() => buildCalendarDays(visibleMonth), [visibleMonth]);
  const weekdayLabels = useMemo(() => getWeekdayLabels(locale), [locale]);
  const timeOptions = useMemo(() => buildTimeOptions(), []);
  const yearOptions = useMemo(() => buildYearOptions(visibleYear), [visibleYear]);

  useEffect(() => {
    if (!openPanel || !rootRef.current) return;
    const rect = rootRef.current.getBoundingClientRect();
    // Date panel is ~22rem tall, time panel ~13rem
    const maxPanelHeight = openPanel === "date" ? 22 * 16 : 13 * 16;
    const spaceBelow = window.innerHeight - rect.bottom;
    const spaceAbove = rect.top;
    setPlacement(spaceBelow < maxPanelHeight && spaceAbove > spaceBelow ? "up" : "down");
  }, [openPanel, datePanelMode]);

  useEffect(() => {
    if (!autoFocus) return;
    dateInputRef.current?.focus();
    dateInputRef.current?.select();
    setOpenPanel("date");
    setDatePanelMode("days");
  }, [autoFocus]);

  useEffect(() => {
    if (selectedDate) {
      setVisibleMonth(getMonthKey(selectedDate));
    }
  }, [selectedDate]);

  function commit() {
    const parsedDate = parseDateDraft(dateDraft, locale);
    const parsedTime = isDateTime ? parseTimeDraft(timeDraft) : null;
    const empty = dateDraft.trim() === "" && (!isDateTime || timeDraft.trim() === "");

    if (empty && (column.nullable ?? true)) {
      onCommit(null);
      return;
    }

    if (!parsedDate) {
      onCancel();
      return;
    }

    if (isDateTime) {
      if (!parsedTime) {
        onCancel();
        return;
      }
      onCommit(combineDateTime(parsedDate, parsedTime));
      return;
    }

    onCommit(parsedDate);
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      setOpenPanel(null);
      setDatePanelMode("days");
      onCancel();
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      setOpenPanel(null);
      setDatePanelMode("days");
      commit();
      return;
    }
    if (
      event.key === "Backspace" &&
      dateDraft === "" &&
      (!isDateTime || timeDraft === "") &&
      (column.nullable ?? true)
    ) {
      event.preventDefault();
      setOpenPanel(null);
      setDatePanelMode("days");
      onCommit(null);
    }
  }

  function handleRootBlur(_event: FocusEvent<HTMLDivElement>) {
    window.setTimeout(() => {
      const active = document.activeElement;
      if (active instanceof Node && rootRef.current?.contains(active)) return;
      setOpenPanel(null);
      setDatePanelMode("days");
      commit();
    }, 0);
  }

  function handleDatePick(isoDate: string) {
    setDateDraft(formatDateDraft(isoDate, locale));
    setDatePanelMode("days");
    if (isDateTime) {
      timeInputRef.current?.focus();
      timeInputRef.current?.select();
      setOpenPanel("time");
      return;
    }
    setOpenPanel(null);
  }

  function handleTimePick(timeValue: string) {
    setTimeDraft(timeValue);
    setOpenPanel(null);
    setDatePanelMode("days");
    timeInputRef.current?.focus();
    timeInputRef.current?.select();
  }

  function stepDatePanel(delta: number) {
    if (datePanelMode === "years") {
      setVisibleMonth(buildMonthKey(visibleYear + delta * 12, visibleMonthIndex));
      return;
    }
    if (datePanelMode === "months") {
      setVisibleMonth(buildMonthKey(visibleYear + delta, visibleMonthIndex));
      return;
    }
    setVisibleMonth((current) => shiftMonth(current, delta));
  }

  function swallowMouseDown(event: MouseEvent<HTMLElement>) {
    event.preventDefault();
  }

  return (
    <EditorShell kind="mono" error={error}>
      <div
        ref={rootRef}
        className={[
          "cell-temporal-editor",
          isDateTime ? "cell-temporal-editor--datetime" : "",
          openPanel ? "cell-temporal-editor--open" : "",
        ]
          .filter(Boolean)
          .join(" ")}
        onBlur={handleRootBlur}
      >
        <input
          ref={dateInputRef}
          type="text"
          inputMode="numeric"
          className="cell-temporal-editor__input cell-temporal-editor__input--date"
          value={dateDraft}
          onFocus={() => {
            dateInputRef.current?.select();
            setOpenPanel("date");
            setDatePanelMode("days");
          }}
          onChange={(event) => setDateDraft(event.target.value)}
          onKeyDown={handleKeyDown}
          aria-label={
            isDateTime
              ? `${column.displayName ?? column.name} date`
              : column.displayName ?? column.name
          }
          aria-invalid={Boolean(error) || undefined}
          placeholder={formatDateDraft("2006-11-24", locale)}
          autoComplete="off"
          size={1}
        />
        {isDateTime ? (
          <>
            <span className="cell-temporal-editor__separator" aria-hidden="true">
              {" "}
            </span>
            <input
              ref={timeInputRef}
              type="text"
              inputMode="numeric"
              className="cell-temporal-editor__input cell-temporal-editor__input--time"
              value={timeDraft}
              onFocus={() => {
                timeInputRef.current?.select();
                setOpenPanel("time");
                setDatePanelMode("days");
              }}
              onChange={(event) => setTimeDraft(event.target.value)}
              onKeyDown={handleKeyDown}
              aria-label={`${column.displayName ?? column.name} time`}
              aria-invalid={Boolean(error) || undefined}
              placeholder="00:00"
              autoComplete="off"
              size={1}
            />
          </>
        ) : null}

        {openPanel === "date" ? (
          <div
            className={[
              "cell-temporal-editor__panel",
              "cell-temporal-editor__panel--date",
              placement === "up" ? "cell-temporal-editor__panel--up" : "",
            ].filter(Boolean).join(" ")}
            role="dialog"
            aria-label="Date picker"
            onMouseDown={swallowMouseDown}
          >
            <div className="cell-temporal-editor__panel-header">
              <button
                type="button"
                className="cell-temporal-editor__nav"
                aria-label={
                  datePanelMode === "years"
                    ? "Previous years"
                    : datePanelMode === "months"
                      ? "Previous year"
                      : "Previous month"
                }
                onClick={() => stepDatePanel(-1)}
              >
                <Icon name="chevron-left" size={14} />
              </button>
              <div className="cell-temporal-editor__title-group">
                <button
                  type="button"
                  className={[
                    "cell-temporal-editor__title-button",
                    datePanelMode === "months"
                      ? "cell-temporal-editor__title-button--active"
                      : "",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  aria-label="Choose month"
                  onClick={() =>
                    setDatePanelMode((current) =>
                      current === "months" ? "days" : "months",
                    )
                  }
                >
                  {getMonthName(visibleMonthIndex, locale)}
                </button>
                <button
                  type="button"
                  className={[
                    "cell-temporal-editor__title-button",
                    datePanelMode === "years"
                      ? "cell-temporal-editor__title-button--active"
                      : "",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  aria-label="Choose year"
                  onClick={() =>
                    setDatePanelMode((current) =>
                      current === "years" ? "days" : "years",
                    )
                  }
                >
                  {datePanelMode === "years"
                    ? getYearRangeLabel(visibleYear)
                    : String(visibleYear)}
                </button>
              </div>
              <button
                type="button"
                className="cell-temporal-editor__nav"
                aria-label={
                  datePanelMode === "years"
                    ? "Next years"
                    : datePanelMode === "months"
                      ? "Next year"
                      : "Next month"
                }
                onClick={() => stepDatePanel(1)}
              >
                <Icon name="chevron-right" size={14} />
              </button>
            </div>
            {datePanelMode === "days" ? (
              <>
                <div className="cell-temporal-editor__weekday-row" aria-hidden="true">
                  {weekdayLabels.map((label) => (
                    <span key={label} className="cell-temporal-editor__weekday">
                      {label}
                    </span>
                  ))}
                </div>
                <div className="cell-temporal-editor__calendar-grid">
                  {calendarDays.map((day) => (
                    <button
                      key={day.isoDate}
                      type="button"
                      aria-label={`Pick ${day.isoDate}`}
                      className={[
                        "cell-temporal-editor__day",
                        day.inMonth ? "" : "cell-temporal-editor__day--outside",
                        day.isoDate === selectedDate
                          ? "cell-temporal-editor__day--selected"
                          : "",
                        day.isToday ? "cell-temporal-editor__day--today" : "",
                      ]
                        .filter(Boolean)
                        .join(" ")}
                      onClick={() => handleDatePick(day.isoDate)}
                    >
                      {day.dayOfMonth}
                    </button>
                  ))}
                </div>
              </>
            ) : null}
            {datePanelMode === "months" ? (
              <div className="cell-temporal-editor__choice-grid">
                {Array.from({ length: 12 }, (_, monthIndex) => {
                  const monthKey = buildMonthKey(visibleYear, monthIndex);
                  const selected = monthIndex === visibleMonthIndex;
                  return (
                    <button
                      key={monthKey}
                      type="button"
                      aria-label={`Choose ${getMonthName(monthIndex, locale)}`}
                      className={[
                        "cell-temporal-editor__choice",
                        selected ? "cell-temporal-editor__choice--selected" : "",
                      ]
                        .filter(Boolean)
                        .join(" ")}
                      onClick={() => {
                        setVisibleMonth(monthKey);
                        setDatePanelMode("days");
                      }}
                    >
                      {getMonthButtonLabel(monthIndex, locale)}
                    </button>
                  );
                })}
              </div>
            ) : null}
            {datePanelMode === "years" ? (
              <div className="cell-temporal-editor__choice-grid cell-temporal-editor__choice-grid--years">
                {yearOptions.map((year) => (
                  <button
                    key={year}
                    type="button"
                    aria-label={`Choose ${year}`}
                    className={[
                      "cell-temporal-editor__choice",
                      year === visibleYear ? "cell-temporal-editor__choice--selected" : "",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onClick={() => {
                      setVisibleMonth(buildMonthKey(year, visibleMonthIndex));
                      setDatePanelMode("days");
                    }}
                  >
                    {year}
                  </button>
                ))}
              </div>
            ) : null}
          </div>
        ) : null}

        {isDateTime && openPanel === "time" ? (
          <div
            className={[
              "cell-temporal-editor__panel",
              "cell-temporal-editor__panel--time",
              placement === "up" ? "cell-temporal-editor__panel--up" : "",
            ].filter(Boolean).join(" ")}
            role="dialog"
            aria-label="Time picker"
            onMouseDown={swallowMouseDown}
          >
            <div className="cell-temporal-editor__time-list">
              {timeOptions.map((option) => (
                <button
                  key={option}
                  type="button"
                  aria-label={`Pick ${option}`}
                  className={[
                    "cell-temporal-editor__time-option",
                    option === selectedTime
                      ? "cell-temporal-editor__time-option--selected"
                      : "",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  onClick={() => handleTimePick(option)}
                >
                  {option}
                </button>
              ))}
            </div>
          </div>
        ) : null}
      </div>
    </EditorShell>
  );
}
