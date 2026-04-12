import { SelectField } from "../components/ui/SelectField";
import { Panel } from "../components/ui/Panel";
import { Alert } from "../components/ui/Alert";
import { useAppStore, type ThemeMode } from "../stores/appStore";

export function SettingsPage() {
  const themeMode = useAppStore((state) => state.themeMode);
  const setThemeMode = useAppStore((state) => state.setThemeMode);

  return (
    <Panel
      title="Workspace settings"
      description="This first settings slice proves the shared component/theme baseline for the rest of Phase 2."
      eyebrow="Step 2.1 foundation"
    >
      <div style={{ display: "grid", gap: "1rem", maxWidth: "28rem" }}>
        <SelectField
          label="Theme"
          name="theme"
          value={themeMode}
          onChange={(event) => setThemeMode(event.target.value as ThemeMode)}
          options={[
            { label: "System", value: "system" },
            { label: "Dark", value: "dark" },
            { label: "Light", value: "light" }
          ]}
          message="Theme changes apply immediately through the shared app store."
        />
        <Alert
          tone="info"
          title="Route is now useful"
          message="Settings is no longer just a placeholder card. It now exercises the shared select, panel, and alert primitives that later admin pages can build on."
        />
      </div>
    </Panel>
  );
}
