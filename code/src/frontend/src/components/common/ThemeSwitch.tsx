import { useAppStore, type ThemeMode } from "../../stores/appStore";
import "./ThemeSwitch.css";

const OPTIONS: Array<{ value: ThemeMode; label: string }> = [
  { value: "dark", label: "Dark" },
  { value: "light", label: "Light" },
  { value: "system", label: "System" }
];

/**
 * Segmented Dark / Light / System control bound to the app theme store. Used
 * by the shell top bar and the public setup and login pages.
 */
export function ThemeSwitch({ className = "" }: { className?: string }) {
  const themeMode = useAppStore((state) => state.themeMode);
  const setThemeMode = useAppStore((state) => state.setThemeMode);
  return (
    <div className={["theme-switch", className].filter(Boolean).join(" ")} role="group" aria-label="Appearance">
      {OPTIONS.map((option) => (
        <button
          key={option.value}
          type="button"
          className="theme-switch__btn"
          aria-pressed={themeMode === option.value}
          onClick={() => setThemeMode(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}
