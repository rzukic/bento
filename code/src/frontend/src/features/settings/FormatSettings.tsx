import { useMemo, useState } from "react";
import { ApiClientError } from "../../api/client";
import { saveWorkspaceSettings } from "../../api/settings";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import {
  CURRENCY_CHOICES,
  LOCALE_CHOICES,
  browserLocale,
  currentCurrency,
  localeLabel,
  setUserLocale,
  setWorkspaceFormat,
  userLocale,
  workspaceLocale,
} from "../../lib/preferences";
import { useSetupStore } from "../setup/setupStore";
import { setWorkspaceCalendar, timeZoneChoices, type WeekStart } from "../../lib/calendar";

function localeName(locale: string): string {
  return LOCALE_CHOICES.find((c) => c.value === locale)?.name ?? locale;
}

/** This browser's number format: its own choice or the workspace's. */
export function BrowserFormatRow() {
  const [own, setOwn] = useState(userLocale());
  const fallback = workspaceLocale() ?? browserLocale();
  return (
    <div className="settings__row">
      <span className="settings__label">Numbers and dates</span>
      <select
        className="field__control field__control--compact settings__select"
        aria-label="Numbers and dates in this browser"
        value={own ?? ""}
        onChange={(event) => {
          const value = event.target.value || null;
          setOwn(value);
          setUserLocale(value);
        }}
      >
        <option value="">
          {workspaceLocale() ? "As the workspace" : "As the browser"} ({localeName(fallback)})
        </option>
        {LOCALE_CHOICES.map((c) => (
          <option key={c.value} value={c.value}>
            {localeLabel(c.value)}
          </option>
        ))}
      </select>
    </div>
  );
}

const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

/** Admins: the workspace's number format, the currency of amounts and its calendar. */
export function WorkspaceFormatPanel() {
  const info = useSetupStore((state) => state.info);
  const serverZone = info && !info.time_zone_set ? (info.time_zone ?? "UTC") : null;
  const savedZone = info?.time_zone_set ? (info.time_zone ?? "") : "";
  const [locale, setLocale] = useState(workspaceLocale() ?? "");
  const [currency, setCurrency] = useState(currentCurrency());
  const [timeZone, setTimeZone] = useState(savedZone);
  const [weekStart, setWeekStart] = useState<WeekStart>(info?.week_start ?? "monday");
  const [yearStart, setYearStart] = useState(info?.year_start ?? 1);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const zones = useMemo(() => {
    const all = timeZoneChoices();
    return timeZone && !all.includes(timeZone) ? [timeZone, ...all] : all;
  }, [timeZone]);
  const browserZone = useMemo(() => Intl.DateTimeFormat().resolvedOptions().timeZone, []);
  const changed =
    (locale || null) !== workspaceLocale() ||
    currency !== currentCurrency() ||
    timeZone !== savedZone ||
    weekStart !== (info?.week_start ?? "monday") ||
    yearStart !== (info?.year_start ?? 1);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const result = await saveWorkspaceSettings({ locale: locale || null, currency, timeZone, weekStart, yearStart });
      setWorkspaceFormat(result.locale, result.currency);
      const current = useSetupStore.getState().info;
      if (current) {
        const zone = result.timeZone ?? (current.time_zone_set ? null : current.time_zone);
        useSetupStore.setState({
          info: {
            ...current,
            locale: result.locale,
            currency: result.currency,
            ...(zone ? { time_zone: zone } : {}),
            time_zone_set: !!result.timeZone,
            week_start: result.weekStart ?? weekStart,
            year_start: result.yearStart ?? yearStart,
          },
        });
      }
      setWorkspaceCalendar({ timeZone: result.timeZone ?? (current?.time_zone_set ? undefined : current?.time_zone), weekStart: result.weekStart ?? weekStart, yearStart: result.yearStart ?? yearStart });
      // The status names the zone in effect when none is chosen (the server's).
      if (!result.timeZone && current?.time_zone_set) void useSetupStore.getState().load({ force: true }).catch(() => undefined);
      setSaved(true);
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The settings could not be saved.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <Panel title="Workspace" description="How everyone sees numbers, dates and amounts, unless they choose otherwise for their browser — and the calendar dashboards, rules, reports and imports go by.">
      {error ? <Alert tone="error" message={error} /> : null}
      <div className="settings__row">
        <span className="settings__label">Numbers and dates</span>
        <select className="field__control field__control--compact settings__select" aria-label="Workspace numbers and dates" value={locale} onChange={(event) => setLocale(event.target.value)}>
          <option value="">Each browser's language</option>
          {LOCALE_CHOICES.map((c) => (
            <option key={c.value} value={c.value}>
              {localeLabel(c.value, currency)}
            </option>
          ))}
        </select>
      </div>
      <div className="settings__row">
        <span className="settings__label">Currency of amounts</span>
        <select className="field__control field__control--compact settings__select" aria-label="Workspace currency" value={currency} onChange={(event) => setCurrency(event.target.value)}>
          {CURRENCY_CHOICES.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      </div>
      <div className="settings__row">
        <span className="settings__label">Time zone</span>
        <select className="field__control field__control--compact settings__select" aria-label="Workspace time zone" value={timeZone} onChange={(event) => setTimeZone(event.target.value)}>
          <option value="">{serverZone ? `The server's (${serverZone})` : "The server's"}</option>
          {zones.map((zone) => (
            <option key={zone} value={zone}>
              {zone === browserZone ? `${zone.replace(/_/g, " ")} (this browser)` : zone.replace(/_/g, " ")}
            </option>
          ))}
        </select>
      </div>
      <div className="settings__row">
        <span className="settings__label">Weeks start on</span>
        <select className="field__control field__control--compact settings__select" aria-label="Weeks start on" value={weekStart} onChange={(event) => setWeekStart(event.target.value as WeekStart)}>
          <option value="monday">Monday</option>
          <option value="sunday">Sunday</option>
          <option value="saturday">Saturday</option>
        </select>
      </div>
      <div className="settings__row">
        <span className="settings__label">The year starts in</span>
        <select className="field__control field__control--compact settings__select" aria-label="The year starts in" value={yearStart} onChange={(event) => setYearStart(Number(event.target.value))}>
          {MONTHS.map((month, index) => (
            <option key={month} value={index + 1}>
              {index === 0 ? "January (calendar year)" : `${month} (fiscal year)`}
            </option>
          ))}
        </select>
      </div>
      <p className="settings__hint">
        “Today”, “this week” and “this year”, the periods of rules and reports and the times of imports and price updates follow these. Quarters start with the year's first month.
      </p>
      <div className="settings__row">
        <span className="settings__label">
          Example: {new Intl.NumberFormat(locale || browserLocale(), { style: "currency", currency }).format(-1250)}
        </span>
        <Button type="button" variant="primary" size="sm" disabled={saving || !changed} onClick={() => void save()}>
          {saving ? "Saving…" : saved && !changed ? "Saved" : "Save"}
        </Button>
      </div>
    </Panel>
  );
}
