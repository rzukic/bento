import { useState } from "react";
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

/** Admins: the workspace's number format and the currency of amounts. */
export function WorkspaceFormatPanel() {
  const [locale, setLocale] = useState(workspaceLocale() ?? "");
  const [currency, setCurrency] = useState(currentCurrency());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const changed = (locale || null) !== workspaceLocale() || currency !== currentCurrency();

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const result = await saveWorkspaceSettings({ locale: locale || null, currency });
      const info = useSetupStore.getState().info;
      if (info) useSetupStore.setState({ info: { ...info, locale: result.locale, currency: result.currency } });
      setWorkspaceFormat(result.locale, result.currency);
      setSaved(true);
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The settings could not be saved.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <Panel title="Workspace" description="How everyone sees numbers, dates and amounts, unless they choose otherwise for their browser.">
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
